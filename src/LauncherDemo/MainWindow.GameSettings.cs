using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private bool _gameDisplaySettingsLoaded;
    private bool _loadingGameDisplaySettings;
    private bool _advancedDisplayControlsAdded;

    private readonly ComboBox _displayModeComboBox = new();
    private readonly ComboBox _vSyncComboBox = new();

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
        EnsureAdvancedDisplayControls();

        var settings = LoadGameDisplayPreferences();
        SelectComboValue(ResolutionComboBox, settings.Resolution, "Default (game controlled)");
        SelectComboValue(FrameRateComboBox, settings.FrameRate, "Unlimited");
        SelectComboValue(_displayModeComboBox, settings.DisplayMode, "Borderless Fullscreen");
        SelectComboValue(_vSyncComboBox, settings.VSync, "On");

        _loadingGameDisplaySettings = false;
        ApplyDxvkEnvironment(settings);

        try
        {
            await ApplyDisplayModeAsync(settings);
            UpdateGameDisplayStatus(settings);
        }
        catch (Exception ex)
        {
            GameDisplayStatusText.Text = $"Could not apply display settings: {ex.Message}";
            GameDisplayStatusText.Foreground = Bad;
        }
    }

    private void EnsureAdvancedDisplayControls()
    {
        if (_advancedDisplayControlsAdded)
            return;

        if (SettingsPage.Content is not StackPanel settingsRoot)
            return;

        var gameDisplayBorder = settingsRoot.Children.OfType<Border>().FirstOrDefault();
        if (gameDisplayBorder?.Child is not StackPanel gameDisplayStack)
            return;

        _advancedDisplayControlsAdded = true;

        _displayModeComboBox.Items.Add(new ComboBoxItem { Content = "Borderless Fullscreen" });
        _displayModeComboBox.Items.Add(new ComboBoxItem { Content = "Windowed" });
        _displayModeComboBox.SelectionChanged += GameDisplaySettingChanged;

        _vSyncComboBox.Items.Add(new ComboBoxItem { Content = "On" });
        _vSyncComboBox.Items.Add(new ComboBoxItem { Content = "Off" });
        _vSyncComboBox.SelectionChanged += GameDisplaySettingChanged;

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 14,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var displayModeStack = new StackPanel { Spacing = 6 };
        displayModeStack.Children.Add(new TextBlock
        {
            Text = "DISPLAY MODE",
            Foreground = new SolidColorBrush(Color.Parse("#8D8D8D")),
            FontSize = 10
        });
        displayModeStack.Children.Add(_displayModeComboBox);
        displayModeStack.Children.Add(new TextBlock
        {
            Text = "Borderless Fullscreen keeps Free Realms windowed internally while filling the primary display.",
            Foreground = new SolidColorBrush(Color.Parse("#747474")),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap
        });
        row.Children.Add(displayModeStack);

        var vsyncStack = new StackPanel { Spacing = 6 };
        vsyncStack.Children.Add(new TextBlock
        {
            Text = "V-SYNC",
            Foreground = new SolidColorBrush(Color.Parse("#8D8D8D")),
            FontSize = 10
        });
        vsyncStack.Children.Add(_vSyncComboBox);
        vsyncStack.Children.Add(new TextBlock
        {
            Text = "Synchronizes presentation to the display refresh cycle to reduce tearing.",
            Foreground = new SolidColorBrush(Color.Parse("#747474")),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(vsyncStack, 1);
        row.Children.Add(vsyncStack);

        var insertAt = Math.Max(0, gameDisplayStack.Children.Count - 1);
        gameDisplayStack.Children.Insert(insertAt, row);
    }

    private async void GameDisplaySettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingGameDisplaySettings || !_gameDisplaySettingsLoaded)
            return;

        var settings = new GameDisplayPreferences
        {
            Resolution = SelectedComboValue(ResolutionComboBox, "Default (game controlled)"),
            FrameRate = SelectedComboValue(FrameRateComboBox, "Unlimited"),
            DisplayMode = SelectedComboValue(_displayModeComboBox, "Borderless Fullscreen"),
            VSync = SelectedComboValue(_vSyncComboBox, "On")
        };

        SaveGameDisplayPreferences(settings);
        ApplyDxvkEnvironment(settings);

        GameDisplayStatusText.Text = "Applying display settings…";
        GameDisplayStatusText.Foreground = Muted;

        try
        {
            await ApplyDisplayModeAsync(settings);
            UpdateGameDisplayStatus(settings);
        }
        catch (Exception ex)
        {
            GameDisplayStatusText.Text = $"Could not apply display settings: {ex.Message}";
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

    private static void ApplyDxvkEnvironment(GameDisplayPreferences settings)
    {
        var fps = ParseFrameRate(settings.FrameRate);
        var syncInterval = IsVSyncEnabled(settings.VSync) ? 1 : 0;

        Environment.SetEnvironmentVariable("DXVK_FRAME_RATE", fps.ToString());
        Environment.SetEnvironmentVariable(
            "DXVK_CONFIG",
            $"d3d9.presentInterval = {syncInterval}; dxgi.syncInterval = {syncInterval}");
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

    private async Task ApplyDisplayModeAsync(GameDisplayPreferences settings)
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

        var borderless = IsBorderlessFullscreen(settings.DisplayMode);
        var resolution = settings.Resolution;

        if (borderless)
        {
            var primary = Screens.Primary?.Bounds;
            var nativeWidth = primary?.Width ?? 1920;
            var nativeHeight = primary?.Height ?? 1080;
            resolution = $"{nativeWidth}x{nativeHeight}";

            await RunProtonRegistryCommandAsync(
                protonPath,
                steamRoot,
                prefixPath,
                new[] { "add", @"HKCU\Software\Wine\X11 Driver", "/v", "Decorated", "/t", "REG_SZ", "/d", "N", "/f" });
        }
        else
        {
            await RunProtonRegistryCommandAsync(
                protonPath,
                steamRoot,
                prefixPath,
                new[] { "add", @"HKCU\Software\Wine\X11 Driver", "/v", "Decorated", "/t", "REG_SZ", "/d", "Y", "/f" },
                ignoreFailure: true);
        }

        if (!borderless && string.Equals(resolution, "Default (game controlled)", StringComparison.OrdinalIgnoreCase))
        {
            await RunProtonRegistryCommandAsync(
                protonPath,
                steamRoot,
                prefixPath,
                new[] { "delete", @"HKCU\Software\Wine\Explorer", "/v", "Desktop", "/f" },
                ignoreFailure: true);
            return;
        }

        if (!TryParseResolution(resolution, out _, out _))
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
            new[] { "add", @"HKCU\Software\Wine\Explorer\Desktops", "/v", "Sanctuary", "/t", "REG_SZ", "/d", resolution, "/f" });
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
        var resolutionText = IsBorderlessFullscreen(settings.DisplayMode)
            ? "native borderless resolution"
            : string.Equals(settings.Resolution, "Default (game controlled)", StringComparison.OrdinalIgnoreCase)
                ? "game-controlled resolution"
                : settings.Resolution;
        var vsyncText = IsVSyncEnabled(settings.VSync) ? "V-Sync on" : "V-Sync off";

        GameDisplayStatusText.Text = $"Active for next launch: {settings.DisplayMode} • {resolutionText} • {fpsText} • {vsyncText}.";
        GameDisplayStatusText.Foreground = Good;
    }
}
