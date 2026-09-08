using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private bool _gameDisplaySettingsLoaded;
    private bool _loadingGameDisplaySettings;
    private bool _vSyncControlAdded;

    private readonly ComboBox _vSyncComboBox = new();

    private string GameDisplayPreferencesPath => Path.Combine(_launcherStateDirectory, "game-display.json");

    private sealed class GameDisplayPreferences
    {
        public string FrameRate { get; set; } = "Unlimited";
        public string VSync { get; set; } = "On";

        // Kept only so the current launch code remains backwards compatible
        // with old preference files. These settings are intentionally ignored.
        public string Resolution { get => "Disabled"; set { } }
        public string DisplayMode { get => "Disabled"; set { } }
    }

    private void GameDisplaySettingsLoaded(object? sender, RoutedEventArgs e)
    {
        if (_gameDisplaySettingsLoaded)
            return;

        _gameDisplaySettingsLoaded = true;
        _loadingGameDisplaySettings = true;

        HideResolutionControl();
        EnsureVSyncControl();

        var settings = LoadGameDisplayPreferences();
        SelectComboValue(FrameRateComboBox, settings.FrameRate, "Unlimited");
        SelectComboValue(_vSyncComboBox, settings.VSync, "On");

        _loadingGameDisplaySettings = false;
        ApplyLegacyFrameRateEnvironment(settings.FrameRate);
        UpdateGameDisplayStatus(settings);
    }

    private void HideResolutionControl()
    {
        if (ResolutionComboBox.Parent is Control resolutionGroup)
            resolutionGroup.IsVisible = false;

        if (ResolutionComboBox.Parent?.Parent is Grid grid)
            grid.ColumnDefinitions = new ColumnDefinitions("*");

        if (FrameRateComboBox.Parent is Control frameRateGroup)
            Grid.SetColumn(frameRateGroup, 0);
    }

    private void EnsureVSyncControl()
    {
        if (_vSyncControlAdded)
            return;

        if (SettingsPage.Content is not StackPanel settingsRoot)
            return;

        var gameDisplayBorder = settingsRoot.Children.OfType<Border>().FirstOrDefault();
        if (gameDisplayBorder?.Child is not StackPanel gameDisplayStack)
            return;

        _vSyncControlAdded = true;

        _vSyncComboBox.Items.Add(new ComboBoxItem { Content = "On" });
        _vSyncComboBox.Items.Add(new ComboBoxItem { Content = "Off" });
        _vSyncComboBox.SelectionChanged += GameDisplaySettingChanged;

        var vsyncStack = new StackPanel { Spacing = 6 };
        vsyncStack.Children.Add(new TextBlock
        {
            Text = "V-SYNC",
            Foreground = Muted,
            FontSize = 10
        });
        vsyncStack.Children.Add(_vSyncComboBox);
        vsyncStack.Children.Add(new TextBlock
        {
            Text = "Controls DXVK presentation synchronization. Changes take effect on the next launch.",
            Foreground = Muted,
            FontSize = 10,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });

        var insertAt = Math.Max(0, gameDisplayStack.Children.Count - 1);
        gameDisplayStack.Children.Insert(insertAt, vsyncStack);
    }

    private void GameDisplaySettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingGameDisplaySettings || !_gameDisplaySettingsLoaded)
            return;

        var settings = new GameDisplayPreferences
        {
            FrameRate = SelectedComboValue(FrameRateComboBox, "Unlimited"),
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

    private static void ApplyLegacyFrameRateEnvironment(string value)
    {
        var fps = ParseFrameRate(value);
        Environment.SetEnvironmentVariable("DXVK_FRAME_RATE", fps.ToString());
    }

    // Compatibility shims for the current launch path. Resolution and display
    // modes are disabled, so Gamescope can never be selected here.
    private static bool IsBorderlessFullscreen(string value) => false;

    private static (int Width, int Height) GetRequestedGameResolution(GameDisplayPreferences settings) => (0, 0);

    private (int Width, int Height) GetPrimaryDisplaySize()
    {
        var primary = Screens.Primary?.Bounds;
        return (primary?.Width ?? 1920, primary?.Height ?? 1080);
    }

    private static string? FindExecutableInPath(string name) => null;

    private void UpdateGameDisplayStatus(GameDisplayPreferences settings)
    {
        var fps = ParseFrameRate(settings.FrameRate);
        var fpsText = fps == 0 ? "unlimited FPS" : $"{fps} FPS cap";
        var vsyncText = IsVSyncEnabled(settings.VSync) ? "V-Sync on" : "V-Sync off";

        GameDisplayStatusText.Text = $"Active for next launch: {fpsText} • {vsyncText}. Resolution and display mode are temporarily disabled.";
        GameDisplayStatusText.Foreground = Good;
    }
}
