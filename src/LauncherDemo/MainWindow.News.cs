using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private Control? _serverPlayPanel;
    private Grid? _serverLoginOverlay;
    private bool _newsInitialized;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        InitializeNewsHome();
    }

    private void InitializeNewsHome()
    {
        if (_newsInitialized || HomePage.Content is not StackPanel oldHome)
            return;

        _newsInitialized = true;

        while (oldHome.Children.Count > 6)
            oldHome.Children.RemoveAt(oldHome.Children.Count - 1);

        if (oldHome.Children.Count >= 3)
        {
            oldHome.Children.RemoveAt(2);
            oldHome.Children.RemoveAt(1);
            oldHome.Children.RemoveAt(0);
        }

        oldHome.Margin = new Thickness(0);
        _serverPlayPanel = oldHome;
        HomePage.Content = BuildNewsPage();

        foreach (var text in this.GetVisualDescendants().OfType<TextBlock>())
        {
            if (string.Equals(text.Text, "HOME", StringComparison.Ordinal))
                text.Text = "NEWS";
        }

        foreach (var button in this.GetVisualDescendants().OfType<Button>())
        {
            var label = button.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(text => string.Equals(text.Text, "ABOUT", StringComparison.Ordinal));
            if (label is not null)
                button.IsVisible = false;
        }
    }

    private Control BuildNewsPage()
    {
        var root = new StackPanel { Margin = new Thickness(34, 30), Spacing = 18 };
        root.Children.Add(new TextBlock
        {
            Text = "NEWS",
            FontSize = 25,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White
        });
        root.Children.Add(new TextBlock
        {
            Text = "Sanctuary Linux Launcher news and announcements.",
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        });

        var welcome = new StackPanel { Spacing = 8 };
        welcome.Children.Add(new TextBlock
        {
            Text = "WELCOME TO SANCTUARY LINUX LAUNCHER",
            FontSize = 17,
            FontWeight = FontWeight.Bold,
            Foreground = Good
        });
        welcome.Children.Add(new TextBlock
        {
            Text = "Launcher announcements, release notes, Linux compatibility notices, and other project updates will live here. Server login opens as an in-launcher overlay from the Servers page.",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap
        });
        welcome.Children.Add(new TextBlock
        {
            Text = "News delivery is not wired to a remote feed yet. This page is ready for that next step.",
            Foreground = Muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        });

        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#151515")),
            BorderBrush = new SolidColorBrush(Color.Parse("#303030")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(22),
            Child = welcome
        });

        return root;
    }

    private async Task OpenServerPlayPopupAsync(SavedServerListItem selected)
    {
        if (_serverPlayPanel is null || Content is not Grid windowRoot)
            return;

        ServerUrlBox.Text = selected.Url;
        LoadRememberedForCurrentServer();
        ShowPage(ServersPage);

        if (_serverLoginOverlay is not null)
        {
            await JoinCurrentServerAsync();
            return;
        }

        var modalCard = new StackPanel
        {
            Spacing = 16,
            MaxWidth = 620
        };

        var serverDescription = new TextBlock
        {
            Text = "Loading server description…",
            FontSize = 11,
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360
        };

        var modeLabel = new TextBlock
        {
            Text = "SIGN IN TO PLAY",
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = Good
        };

        var titleBlock = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock
                {
                    Text = selected.DisplayName,
                    FontSize = 20,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White
                },
                serverDescription,
                modeLabel
            }
        };

        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var registerButton = new Button
        {
            Content = "CREATE ACCOUNT",
            Padding = new Thickness(14, 7)
        };

        var closeButton = new Button
        {
            Content = "CLOSE",
            Padding = new Thickness(12, 7)
        };

        headerActions.Children.Add(registerButton);
        headerActions.Children.Add(closeButton);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(titleBlock);
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);

        modalCard.Children.Add(header);
        modalCard.Children.Add(_serverPlayPanel);

        var cardBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#171717")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3A3A3A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(24),
            Width = 620,
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = modalCard
        };

        var overlay = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(205, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ZIndex = 1000
        };
        Grid.SetColumn(overlay, 1);
        overlay.Children.Add(cardBorder);
        _serverLoginOverlay = overlay;
        windowRoot.Children.Add(overlay);

        Control? registrationPanel = null;

        void ShowSignIn()
        {
            if (registrationPanel is not null)
                modalCard.Children.Remove(registrationPanel);

            if (!modalCard.Children.Contains(_serverPlayPanel))
                modalCard.Children.Add(_serverPlayPanel);

            modeLabel.Text = "SIGN IN TO PLAY";
            registerButton.Content = "CREATE ACCOUNT";
        }

        void ShowRegistration()
        {
            modalCard.Children.Remove(_serverPlayPanel);
            registrationPanel ??= BuildRegistrationPanel(selected, ShowSignIn);

            if (!modalCard.Children.Contains(registrationPanel))
                modalCard.Children.Add(registrationPanel);

            modeLabel.Text = "CREATE A SERVER ACCOUNT";
            registerButton.Content = "BACK TO SIGN IN";
        }

        void CloseOverlay()
        {
            if (_serverLoginOverlay is null)
                return;

            modalCard.Children.Remove(_serverPlayPanel);
            if (registrationPanel is not null)
                modalCard.Children.Remove(registrationPanel);
            windowRoot.Children.Remove(_serverLoginOverlay);
            _serverLoginOverlay = null;
        }

        registerButton.Click += (_, _) =>
        {
            if (modalCard.Children.Contains(_serverPlayPanel))
                ShowRegistration();
            else
                ShowSignIn();
        };

        closeButton.Click += (_, _) => CloseOverlay();

        try
        {
            await JoinCurrentServerAsync();
            var description = ServerDescriptionText.Text?.Trim();
            serverDescription.Text = string.IsNullOrWhiteSpace(description)
                ? "No server description provided."
                : description;
        }
        catch
        {
            serverDescription.Text = "Server description unavailable.";
        }
    }
}
