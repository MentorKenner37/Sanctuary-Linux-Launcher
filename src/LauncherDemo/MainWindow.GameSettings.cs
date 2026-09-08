using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
    private string LegacyDesktopCleanupMarkerPath => Path.Combine(_launcherStateDirectory, ".wine-desktop-disabled");

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
        ApplyLegacyFrameRateEnvironment(settings.FrameRate);

        try
        {
            await DisableLegacyWineVirtualDesktopOnceAsync();
        }
        catch
        {
            // The launch path no longer relies on Wine's virtual desktop, so a
            // cleanup failure should not prevent the launcher from opening.
        }

        UpdateGameDisplayStatus(settings);
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
            Text = "Borderless Fullscreen uses Gamescope. Windowed launches directly through Proton so the window can be resized and maximized normally.",
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
            Text = "Controls DXVK presentation synchronization. Changes take effect on the next launch.",
            Foreground = new SolidColorBrush(Color.Parse("#747474")),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(vsyncStack, 1);
        row.Children.Add(vsyncStack);

        var insertAt = Math.Max(0, gameDisplayStack.Children.Count - 1);
        gameDisplayStack.Children.Insert(insertAt, row);
    }

    private void GameDisplaySettingChanged(object? sender, SelectionChangedEventArgs e)
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
        ApplyLegacyFrameRateEnvironment(settings.FrameRate);
        UpdateGameDisplayStatus(settings);
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

    private static void ApplyLegacyFrameRateEnvironment(string value)
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

    private (int Width, int Height) GetPrimaryDisplaySize()
    {
        var primary = Screens.Primary?.Bounds;
        return (primary?.Width ?? 1920, primary?.Height ?? 1080);
    }

    private (int Width, int Height) GetRequestedGameResolution(GameDisplayPreferences settings)
    {
        // Windowed mode intentionally bypasses Gamescope so Cinnamon/Wine owns
        // the real top-level window. That lets maximize/restore resize the game
        // instead of scaling it inside a fixed Gamescope canvas with letterboxing.
        if (!IsBorderlessFullscreen(settings.DisplayMode))
            return (0, 0);

        if (TryParseResolution(settings.Resolution, out var width, out var height))
            return (width, height);

        return GetPrimaryDisplaySize();
    }

    private static string? FindExecutableInPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
            }
        }

        return null;
    }

    private async Task DisableLegacyWineVirtualDesktopOnceAsync()
    {
        if (File.Exists(LegacyDesktopCleanupMarkerPath))
            return;

        var protonPath = ReadRuntimeConfig("proton-path.txt");
        var steamRoot = ReadRuntimeConfig("steam-path.txt");
        var prefixPath = ReadRuntimeConfig("prefix-path.txt");

        if (string.IsNullOrWhiteSpace(protonPath) || !File.Exists(protonPath))
            return;
        if (string.IsNullOrWhiteSpace(steamRoot) || !Directory.Exists(steamRoot))
            return;
        if (string.IsNullOrWhiteSpace(prefixPath))
            prefixPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "OSFR-Linux", "ProtonPrefix");

        Directory.CreateDirectory(prefixPath);

        await RunProtonRegistryCommandAsync(
            protonPath,
            steamRoot,
            prefixPath,
            new[] { "delete", @"HKCU\Software\Wine\Explorer", "/v", "Desktop", "/f" },
            ignoreFailure: true);

        await RunProtonRegistryCommandAsync(
            protonPath,
            steamRoot,
            prefixPath,
            new[] { "add", @"HKCU\Software\Wine\X11 Driver", "/v", "Decorated", "/t", "REG_SZ", "/d", "Y", "/f" },
            ignoreFailure: true);

        Directory.CreateDirectory(_launcherStateDirectory);
        File.WriteAllText(LegacyDesktopCleanupMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
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
            ? string.Equals(settings.Resolution, "Default (game controlled)", StringComparison.OrdinalIgnoreCase)
                ? "native display resolution"
                : settings.Resolution
            : "resizable window";
        var vsyncText = IsVSyncEnabled(settings.VSync) ? "V-Sync on" : "V-Sync off";
        var gamescopePath = FindExecutableInPath("gamescope");
        var gamescopeText = IsBorderlessFullscreen(settings.DisplayMode)
            ? gamescopePath is null
                ? "Gamescope not detected; borderless falls back to normal windowed mode"
                : "Gamescope ready"
            : "direct Proton window";

        GameDisplayStatusText.Text = $"Active for next launch: {settings.DisplayMode} • {resolutionText} • {fpsText} • {vsyncText} • {gamescopeText}.";
        GameDisplayStatusText.Foreground = gamescopePath is null && IsBorderlessFullscreen(settings.DisplayMode) ? Muted : Good;
    }
}
