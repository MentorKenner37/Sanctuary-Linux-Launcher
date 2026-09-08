using Avalonia.Interactivity;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private async void JoinServerClicked(object? sender, RoutedEventArgs e)
    {
        LoadRememberedForCurrentServer();
        await JoinCurrentServerAsync();
    }

    private async void JoinSavedServerClicked(object? sender, RoutedEventArgs e)
    {
        if (SavedServersList.SelectedItem is not SavedServerListItem selected)
        {
            ServersStatusText.Text = "Select a server first.";
            ServersStatusText.Foreground = Bad;
            return;
        }

        if (!selected.Online)
        {
            ServersStatusText.Text = "That server is currently offline.";
            ServersStatusText.Foreground = Bad;
            return;
        }

        ServersStatusText.Text = $"Opening {selected.DisplayName}…";
        ServersStatusText.Foreground = Good;
        await OpenServerPlayPopupAsync(selected);
    }

    private async Task JoinCurrentServerAsync()
    {
        if (!TryNormalizeServerUrl(ServerUrlBox.Text, out var serverBaseUri, out var error))
        {
            SetConnectionState(error, false);
            return;
        }

        SetBusy(true);
        LaunchButton.IsEnabled = false;
        OnlineText.Text = "CONNECTING";
        OnlineText.Foreground = Muted;
        ConnectionStatusText.Text = "Loading server information…";
        ConnectionStatusText.Foreground = Muted;
        ClientStatusText.Text = "Waiting for server connection.";
        ClientStatusText.Foreground = Muted;

        try
        {
            var manifestUri = new Uri(serverBaseUri, "servermanifest.xml");
            var xml = await DownloadTextLimitedAsync(manifestUri, MaxManifestBytes);
            var manifest = ParseServerManifest(xml);

            if (!Uri.TryCreate(manifest.WebApiUrl, UriKind.Absolute, out var apiUri) || apiUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("This server's WebApiUrl is not HTTPS. Login is blocked.");

            // Client storage in the recovered launcher is keyed by manifest.Name.
            // Make that internal key unique per server host while keeping the clean
            // manifest name in the UI. This prevents different servers from sharing
            // and re-hashing the same Client directory.
            var storageName = $"{manifest.Name} [{serverBaseUri.Host}{(serverBaseUri.IsDefaultPort ? string.Empty : $"_{serverBaseUri.Port}")}]";
            var storageManifest = manifest with { Name = storageName };

            _serverBaseUri = serverBaseUri;
            _serverManifest = storageManifest;

            ServerNameText.Text = manifest.Name;
            ServerDescriptionText.Text = manifest.Description;
            ManifestVersionText.Text = $"Manifest: v{manifest.Version} (v2 compatible)";
            WebApiText.Text = manifest.WebApiUrl;
            LoginServerText.Text = manifest.LoginServer;

            OnlineText.Text = "CONNECTED";
            OnlineText.Foreground = Good;
            SetConnectionState($"Connected to {manifest.Name}. Preparing client automatically…", true);

            LaunchButton.IsEnabled = _gameProcess is null;

            await TryLoadServerLogoAsync(serverBaseUri, manifest.LogoUrl);

            try
            {
                await VerifyAndUpdateClientAsync(serverBaseUri, storageManifest);
                SetConnectionState($"{manifest.Name} is ready to play.", true);
            }
            catch (Exception ex)
            {
                ClientStatusText.Text = $"Automatic client preparation failed: {ex.Message}";
                ClientStatusText.Foreground = Bad;
                SetConnectionState($"Connected to {manifest.Name}. Client verification will retry when you launch.", true);
            }
        }
        catch (Exception ex)
        {
            _serverBaseUri = null;
            _serverManifest = null;
            OnlineText.Text = "OFFLINE";
            OnlineText.Foreground = Bad;
            ServerNameText.Text = "No server connected";
            ServerDescriptionText.Text = "Join a server to load its metadata.";
            ManifestVersionText.Text = "Manifest: —";
            SetConnectionState(ex.Message, false);
            ClientStatusText.Text = "Could not prepare the client because the server connection failed.";
            ClientStatusText.Foreground = Bad;
        }
        finally
        {
            ClientProgress.IsVisible = false;
            SetBusy(false);
            LaunchButton.IsEnabled = _serverManifest is not null && _gameProcess is null;
        }
    }
}
