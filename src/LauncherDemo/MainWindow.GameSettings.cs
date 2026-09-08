using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private bool _gameDisplaySettingsLoaded;
    private bool _loadingGameDisplaySettings;

    private string GameDisplayPreferencesPath => Path.Combine(_launcherStateDirectory, "game-display.json");

    private sealed class GameDisplayPreferences
    {
        public string Resolution { get; set; } = "Default (game controlled)";
        public string FrameRate { get; set; } = "Unlimited";
        public string DisplayMode { get; set; } = "Borderless Fullscreen";
        public string VSync { get; set; } = "On";
    }

    private async void GameDisplaySettingsLoaded(object? sender, RoutedEventArgs e)
    {
        if (_gameDisplaySettingsLoaded)
            return;

        _gameDisplaySettingsLoaded = true;
        _loadingGameDisplaySettings = true;

        var settings = LoadGameDisplayPreferences();
        SelectComboValue(ResolutionComboBox, settings.Resolution, "Default (game controlled)");
        SelectComboValue(FrameRateComboBox, settings.FrameRate, "Unlimited");
        SelectComboValue(DisplayModeComboBox, settings.DisplayMode, "Borderless Fullscreen");
        SelectComboValue(VSyncComboBox, settings.VSync, "On");

        _loadingGameDisplaySettings = false;
        ApplyFrameRateLimit(settings.FrameRate);

        if (!string.Equals(settings.Resolution, "Default (game controlled)", StringComparison.OrdinalIgnoreCase))
            await ApplyResolutionAsync(settings.Resolution);

        UpdateGameDisplayStatus(settings);
    }

    private async void GameDisplaySettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingGameDisplaySettings || !_gameDisplaySettingsLoaded)
            return;

        var settings = new GameDisplayPreferences
        {
            Resolution = SelectedComboValue(ResolutionComboBox, "Default (game controlled)"),
            FrameRate = SelectedComboValue(FrameRateComboBox, "Unlimited"),
            DisplayMode = SelectedComboValue(DisplayModeComboBox, "Borderless Fullscreen"),
            VSync = SelectedComboValue(VSyncComboBox, "On")
        };

        SaveGameDisplayPreferences(settings);
        ApplyFrameRateLimit(settings.FrameRate);

        GameDisplayStatusText.Text = "Applying display settings…";
        GameDisplayStatusText.Foreground = Muted;

        try
        {
            await ApplyResolutionAsync(settings.Resolution);
            UpdateGameDisplayStatus(settings);
        }
        catch (Exception ex)
        {
            GameDisplayStatusText.Text = $"Could not apply resolution: {ex.Message}";
            GameDisplayStatusText.Foreground = Bad;
        }
    }

    private GameDisplayPreferences LoadGameDisplayPreferences()
    {
        try
        {
            if (File.Exists(GameDisplayPreferencesPath))
                return JsonSerializer.Deserialize<GameDisplayPreferences>(File.ReadAllText(GameDisplayPreferencesPath))
                    ?? new GameDisplayPreferences();
        }
        catch
        {
        }

        return new GameDisplayPreferences();
    }

    private void SaveGameDisplayPreferences(GameDisplayPreferences settings)
    {
        try
        {
            Directory.CreateDirectory(_launcherStateDirectory);
            File.WriteAllText(
                GameDisplayPreferencesPath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private static string SelectedComboValue(ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is ComboBoxItem item && item.Content is not null
            ? item.Content.ToString() ?? fallback
            : fallback;
    }

    private static void SelectComboValue(ComboBox comboBox, string desired, string fallback)
    {
        ComboBoxItem? fallbackItem = null;
        foreach (var entry in comboBox.Items)
        {
            if (entry is not ComboBoxItem item)
                continue;

            var value = item.Content?.ToString() ?? string.Empty;
            if (string.Equals(value, fallback, StringComparison.OrdinalIgnoreCase))
                fallbackItem = item;
            if (string.Equals(value, desired, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedItem = fallbackItem;
    }

    private static int ParseFrameRate(string value)
    {
        if (string.Equals(value, "Unlimited", StringComparison.OrdinalIgnoreCase))
            return 0;

        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var fps) && fps > 0 ? fps : 0;
    }

    private static bool IsVSyncEnabled(string value) =>
        !string.Equals(value, "Off", StringComparison.OrdinalIgnoreCase);

    private static bool IsBorderlessFullscreen(string value) =>
        string.Equals(value, "Borderless Fullscreen", StringComparison.OrdinalIgnoreCase);

    private static void ApplyFrameRateLimit(string value)
    {
        var fps = ParseFrameRate(value);
        Environment.SetEnvironmentVariable("DXVK_FRAME_RATE", fps.ToString());
    }

    private static bool TryParseResolution(string value, out int width, out int height)
    {
        width = 0;
        height = 0;
        var parts = value.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], out width)
            && int.TryParse(parts[1], out height)
            && width >= 640
            && height >= 480;
    }

    private async Task ApplyResolutionAsync(string value)
    {
        var protonPath = ReadRuntimeConfig("proton-path.txt");
        var steamRoot = ReadRuntimeConfig("steam-path.txt");
        var prefixPath = ReadRuntimeConfig("prefix-path.txt");

        if (string.IsNullOrWhiteSpace(protonPath) || !File.Exists(protonPath))
            throw new FileNotFoundException("Configured Proton runtime was not found.");
        if (string.IsNullOrWhiteSpace(steamRoot) || !Directory.Exists(steamRoot))
            throw new DirectoryNotFoundException("Configured Steam root was not found.");
        if (string.IsNullOrWhiteSpace(prefixPath))
            prefixPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "OSFR-Linux", "ProtonPrefix");

        Directory.CreateDirectory(prefixPath);

        if (string.Equals(value, "Default (game controlled)", StringComparison.OrdinalIgnoreCase))
        {
            await RunProtonRegistryCommandAsync(
                protonPath,
                steamRoot,
                prefixPath,
                new[] { "delete", @"HKCU\Software\Wine\Explorer", "/v", "Desktop", "/f" },
                ignoreFailure: true);
            return;
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{3,4}x\d{3,4}$"))
            throw new InvalidDataException("Invalid resolution selection.");

        await RunProtonRegistryCommandAsync(
            protonPath,
            steamRoot,
            prefixPath,
            new[] { "add", @"HKCU\Software\Wine\Explorer", "/v", "Desktop", "/t", "REG_SZ", "/d", "Sanctuary", "/f" });

        await RunProtonRegistryCommandAsync(
            protonPath,
            steamRoot,
            prefixPath,
            new[] { "add", @"HKCU\Software\Wine\Explorer\Desktops", "/v", "Sanctuary", "/t", "REG_SZ", "/d", value, "/f" });
    }

    private static async Task RunProtonRegistryCommandAsync(
        string protonPath,
        string steamRoot,
        string prefixPath,
        IEnumerable<string> registryArguments,
        bool ignoreFailure = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = protonPath,
                WorkingDirectory = Path.GetDirectoryName(protonPath) ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("reg.exe");
        foreach (var argument in registryArguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.StartInfo.Environment["STEAM_COMPAT_DATA_PATH"] = prefixPath;
        process.StartInfo.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = steamRoot;
        process.StartInfo.Environment["PROTON_LOG"] = "0";

        if (!process.Start())
            throw new InvalidOperationException("Could not start Proton to update the display configuration.");

        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 && !ignoreFailure)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"reg.exe exited with code {process.ExitCode}." : stderr.Trim());
    }

    private void UpdateGameDisplayStatus(GameDisplayPreferences settings)
    {
        var fps = ParseFrameRate(settings.FrameRate);
        var fpsText = fps == 0 ? "unlimited FPS" : $"{fps} FPS cap";
        var resolutionText = string.Equals(settings.Resolution, "Default (game controlled)", StringComparison.OrdinalIgnoreCase)
            ? "game-controlled resolution"
            : settings.Resolution;
        var vsyncText = IsVSyncEnabled(settings.VSync) ? "V-Sync on" : "V-Sync off";

        GameDisplayStatusText.Text = $"Active for next launch: {settings.DisplayMode} • {resolutionText} • {fpsText} • {vsyncText}.";
        GameDisplayStatusText.Foreground = Good;
    }
}
