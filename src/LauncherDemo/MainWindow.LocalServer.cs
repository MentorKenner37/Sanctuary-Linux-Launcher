using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private readonly string _localServerRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "OSFR-Linux", "LocalServer");

    private string LocalServerSourceDirectory => Path.Combine(_localServerRoot, "Sanctuary");
    private string LocalServerComposePath => Path.Combine(_localServerRoot, "docker-compose.local.yml");
    private string LocalAssetProxyDirectory => Path.Combine(_localServerRoot, "AssetProxy");
    private string LocalAssetProxyConfigPath => Path.Combine(LocalAssetProxyDirectory, "nginx.conf");
    private string LocalAssetCacheDirectory => Path.Combine(LocalAssetProxyDirectory, "cache");

    private TextBlock? _localServerStatusText;
    private TextBlock? _localServerDetailsText;
    private Button? _localServerSetupButton;
    private Button? _localServerStartButton;
    private Button? _localServerStopButton;
    private bool _localServerTabInitialized;

    private void InitializeLocalServerTab()
    {
        if (_localServerTabInitialized) return;
        if (ServersPage.Content is not Control existingServersContent) return;
        _localServerTabInitialized = true;

        ServersPage.Content = new TabControl
        {
            Margin = new Thickness(0),
            ItemsSource = new object[]
            {
                new TabItem { Header = "SAVED SERVERS", Content = existingServersContent },
                new TabItem { Header = "LOCAL SERVER", Content = BuildLocalServerPanel() }
            }
        };
    }

    private Control BuildLocalServerPanel()
    {
        _localServerStatusText = new TextBlock { Text = "CHECKING…", Foreground = Muted, FontWeight = FontWeight.Bold };
        _localServerDetailsText = new TextBlock
        {
            Text = "The local server is managed separately from Saved Servers. Add it to Saved Servers yourself when it is ready for client connections.",
            Foreground = new SolidColorBrush(Color.Parse("#9E9E9E")), TextWrapping = TextWrapping.Wrap
        };

        _localServerSetupButton = new Button { Content = "SET UP SERVER" };
        _localServerStartButton = new Button { Content = "START SERVER", Classes = { "primary" } };
        _localServerStopButton = new Button { Content = "STOP SERVER" };
        _localServerSetupButton.Click += LocalServerSetupClicked;
        _localServerStartButton.Click += LocalServerStartClicked;
        _localServerStopButton.Click += LocalServerStopClicked;

        var openFolderButton = new Button { Content = "OPEN SERVER FOLDER" };
        openFolderButton.Click += LocalServerOpenFolderClicked;
        var refreshButton = new Button { Content = "REFRESH STATUS" };
        refreshButton.Click += async (_, _) => await RefreshLocalServerStatusAsync();

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { _localServerSetupButton, _localServerStartButton, _localServerStopButton, refreshButton }
        };

        var panel = new StackPanel { Margin = new Thickness(34, 30), Spacing = 18 };
        panel.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "LOCAL SERVER", FontSize = 25, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#F5F5F5")) },
                new TextBlock { Text = "Set up and control a Sanctuary server that runs only on this computer.", Foreground = new SolidColorBrush(Color.Parse("#9E9E9E")), TextWrapping = TextWrapping.Wrap }
            }
        });
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#151515")), BorderBrush = new SolidColorBrush(Color.Parse("#303030")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 13,
                Children =
                {
                    new TextBlock { Text = "SERVER STATUS", Foreground = Good, FontWeight = FontWeight.Bold, FontSize = 12 },
                    _localServerStatusText, _localServerDetailsText, buttonRow
                }
            }
        });
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#151515")), BorderBrush = new SolidColorBrush(Color.Parse("#303030")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 9,
                Children =
                {
                    new TextBlock { Text = "LOCAL ENDPOINTS", Foreground = Good, FontWeight = FontWeight.Bold, FontSize = 12 },
                    new TextBlock { Text = "Web API: http://127.0.0.1:20040", Foreground = new SolidColorBrush(Color.Parse("#E8E8E8")) },
                    new TextBlock { Text = "Gateway: 127.0.0.1:20260/UDP", Foreground = new SolidColorBrush(Color.Parse("#E8E8E8")) },
                    new TextBlock { Text = "Login: 127.0.0.1:20041–20042/UDP", Foreground = new SolidColorBrush(Color.Parse("#E8E8E8")) },
                    new TextBlock { Text = "Asset cache: http://127.0.0.1:20050/assets", Foreground = new SolidColorBrush(Color.Parse("#E8E8E8")) },
                    new TextBlock { Text = "Assets are cached locally after first use and fetched from Raising Kaines when missing.", Foreground = new SolidColorBrush(Color.Parse("#747474")), FontSize = 11, TextWrapping = TextWrapping.Wrap },
                    openFolderButton
                }
            }
        });
        return new ScrollViewer { Content = panel };
    }

    private async void LocalServerSetupClicked(object? sender, RoutedEventArgs e)
    {
        SetLocalServerBusy(true);
        SetLocalServerStatus("SETTING UP…", Muted, "Preparing Sanctuary, the client mirror, and local asset cache.");
        try
        {
            Directory.CreateDirectory(_localServerRoot);
            Directory.CreateDirectory(LocalAssetProxyDirectory);
            Directory.CreateDirectory(LocalAssetCacheDirectory);
            if (!CommandExists("git")) throw new InvalidOperationException("Git is not installed.");
            if (!CommandExists("docker")) throw new InvalidOperationException("Docker is not installed. Install Docker Engine with the Compose plugin first.");

            if (!Directory.Exists(Path.Combine(LocalServerSourceDirectory, ".git")))
            {
                var clone = await RunProcessAsync("git", _localServerRoot, "clone", "--depth", "1", "https://github.com/Open-Source-Free-Realms/Sanctuary.git", "Sanctuary");
                if (clone.ExitCode != 0) throw new InvalidOperationException("Could not download Sanctuary. " + clone.ErrorMessage);
            }
            else
            {
                var pull = await RunProcessAsync("git", LocalServerSourceDirectory, "pull", "--ff-only");
                if (pull.ExitCode != 0) throw new InvalidOperationException("Could not update Sanctuary. " + pull.ErrorMessage);
            }

            File.WriteAllText(LocalAssetProxyConfigPath, BuildLocalAssetProxyConfig());
            File.WriteAllText(LocalServerComposePath, BuildLocalComposeFile());
            SetLocalServerStatus("READY", Good, "Server files and the nginx asset cache are prepared. Press START SERVER.");
        }
        catch (Exception ex) { SetLocalServerStatus("SETUP FAILED", Bad, ex.Message); }
        finally
        {
            SetLocalServerBusy(false);
            await RefreshLocalServerStatusAsync(preserveReadyMessage: true);
        }
    }

    private async void LocalServerStartClicked(object? sender, RoutedEventArgs e)
    {
        SetLocalServerBusy(true);
        SetLocalServerStatus("STARTING…", Muted, "Building and starting Sanctuary and the local asset cache.");
        try
        {
            if (!File.Exists(LocalServerComposePath) || !Directory.Exists(LocalServerSourceDirectory)) throw new InvalidOperationException("Run SET UP SERVER first.");
            if (!CommandExists("docker")) throw new InvalidOperationException("Docker is not installed.");

            Directory.CreateDirectory(LocalAssetProxyDirectory);
            Directory.CreateDirectory(LocalAssetCacheDirectory);
            File.WriteAllText(LocalAssetProxyConfigPath, BuildLocalAssetProxyConfig());
            File.WriteAllText(LocalServerComposePath, BuildLocalComposeFile());

            // Remove the old one-shot standalone proxy if it exists so Compose can own port 20050.
            await RunProcessAsync("docker", _localServerRoot, "rm", "-f", "sanctuary-asset-proxy");

            var result = await RunProcessAsync("docker", _localServerRoot, "compose", "-f", LocalServerComposePath, "up", "-d", "--build");
            if (result.ExitCode != 0) throw new InvalidOperationException("Docker could not start the server. " + result.ErrorMessage);
            await Task.Delay(1500);
            await RefreshLocalServerStatusAsync();
        }
        catch (Exception ex) { SetLocalServerStatus("START FAILED", Bad, ex.Message); }
        finally { SetLocalServerBusy(false); }
    }

    private async void LocalServerStopClicked(object? sender, RoutedEventArgs e)
    {
        SetLocalServerBusy(true);
        SetLocalServerStatus("STOPPING…", Muted, "Stopping local Sanctuary services. Server data and cached assets will be kept.");
        try
        {
            if (File.Exists(LocalServerComposePath) && CommandExists("docker"))
            {
                var result = await RunProcessAsync("docker", _localServerRoot, "compose", "-f", LocalServerComposePath, "down");
                if (result.ExitCode != 0) throw new InvalidOperationException("Docker could not stop the server. " + result.ErrorMessage);
            }
            SetLocalServerStatus("OFFLINE", Muted, "Local server is stopped. Cached assets were kept.");
        }
        catch (Exception ex) { SetLocalServerStatus("STOP FAILED", Bad, ex.Message); }
        finally { SetLocalServerBusy(false); await RefreshLocalServerStatusAsync(); }
    }

    private void LocalServerOpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_localServerRoot);
            Process.Start(new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { _localServerRoot }, UseShellExecute = false });
        }
        catch (Exception ex) { SetLocalServerStatus("ERROR", Bad, "Could not open the server folder: " + ex.Message); }
    }

    private async Task RefreshLocalServerStatusAsync(bool preserveReadyMessage = false)
    {
        if (_localServerStatusText is null) return;
        if (!Directory.Exists(LocalServerSourceDirectory) || !File.Exists(LocalServerComposePath))
        {
            SetLocalServerStatus("NOT SET UP", Muted, "Press SET UP SERVER to download and prepare the official Sanctuary server emulator locally.");
            if (_localServerStartButton is not null) _localServerStartButton.IsEnabled = false;
            if (_localServerStopButton is not null) _localServerStopButton.IsEnabled = false;
            return;
        }
        if (_localServerStartButton is not null) _localServerStartButton.IsEnabled = true;
        if (!CommandExists("docker"))
        {
            SetLocalServerStatus("DOCKER REQUIRED", Bad, "Server files exist, but Docker is not available.");
            if (_localServerStopButton is not null) _localServerStopButton.IsEnabled = false;
            return;
        }
        try
        {
            var result = await RunProcessAsync("docker", _localServerRoot, "compose", "-f", LocalServerComposePath, "ps", "--status", "running", "-q");
            var count = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            if (count >= 5)
            {
                SetLocalServerStatus("ONLINE", Good, "Sanctuary WebAPI, login, gateway, database, and local asset cache are running.");
                if (_localServerStopButton is not null) _localServerStopButton.IsEnabled = true;
            }
            else if (count > 0)
            {
                SetLocalServerStatus("PARTIAL", Bad, $"{count}/5 local server containers are running.");
                if (_localServerStopButton is not null) _localServerStopButton.IsEnabled = true;
            }
            else
            {
                if (!preserveReadyMessage) SetLocalServerStatus("OFFLINE", Muted, "Server is set up but not running.");
                if (_localServerStopButton is not null) _localServerStopButton.IsEnabled = false;
            }
        }
        catch (Exception ex) { SetLocalServerStatus("STATUS UNKNOWN", Bad, ex.Message); }
    }

    private void SetLocalServerBusy(bool busy)
    {
        if (_localServerSetupButton is not null) _localServerSetupButton.IsEnabled = !busy;
        if (_localServerStartButton is not null) _localServerStartButton.IsEnabled = !busy;
        if (_localServerStopButton is not null) _localServerStopButton.IsEnabled = !busy;
    }

    private void SetLocalServerStatus(string status, IBrush brush, string details)
    {
        if (_localServerStatusText is not null) { _localServerStatusText.Text = status; _localServerStatusText.Foreground = brush; }
        if (_localServerDetailsText is not null) _localServerDetailsText.Text = details;
    }

    private static bool CommandExists(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo { FileName = "which", ArgumentList = { command }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            process?.WaitForExit(1500);
            return process is { ExitCode: 0 };
        }
        catch { return false; }
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string workingDirectory, params string[] arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = fileName, WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException($"Could not start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string BuildLocalAssetProxyConfig() => """
events {}

http {
    proxy_cache_path /cache levels=1:2 keys_zone=frassets:100m max_size=50g inactive=30d use_temp_path=off;

    server {
        listen 20050;
        server_name _;

        location /assets/ {
            proxy_pass http://public-assets.raisingkaines.com;
            proxy_http_version 1.1;
            proxy_set_header Host public-assets.raisingkaines.com;
            proxy_set_header Connection "";
            proxy_set_header Range $http_range;
            proxy_set_header If-Range $http_if_range;
            proxy_set_header If-Modified-Since $http_if_modified_since;
            proxy_set_header If-None-Match $http_if_none_match;
            proxy_pass_request_headers on;
            proxy_redirect off;
            proxy_buffering on;
            proxy_cache frassets;
            proxy_cache_key "$scheme|$proxy_host|$request_uri";
            proxy_cache_valid 200 206 30d;
            proxy_cache_valid 301 302 1h;
            proxy_cache_valid 404 1m;
            proxy_cache_lock on;
            proxy_cache_lock_timeout 30s;
            add_header X-Sanctuary-Asset-Cache $upstream_cache_status always;
        }
    }
}
""";

    private static string BuildLocalComposeFile() => """
services:
  sanctuary.mysql:
    image: mariadb:10.5
    restart: unless-stopped
    environment:
      MYSQL_ROOT_PASSWORD: sanctuary-local-root
      MYSQL_DATABASE: sanctuary
      MYSQL_USER: sanctuary
      MYSQL_PASSWORD: sanctuary
    volumes:
      - db:/var/lib/mysql
    healthcheck:
      test: ["CMD", "healthcheck.sh", "--connect", "--innodb_initialized"]
      interval: 10s
      timeout: 5s
      retries: 10
      start_period: 30s

  sanctuary.assetproxy:
    image: nginx:alpine
    restart: unless-stopped
    ports:
      - "127.0.0.1:20050:20050"
    volumes:
      - ./AssetProxy/nginx.conf:/etc/nginx/nginx.conf:ro
      - ./AssetProxy/cache:/cache

  sanctuary.webapi:
    build:
      context: ./Sanctuary/src
      dockerfile: Docker/Sanctuary.WebAPI.dockerfile
    restart: unless-stopped
    ports:
      - "127.0.0.1:20040:20040"
    volumes: &common-volumes
      - ./Sanctuary/logs:/app/Logs
      - ./Sanctuary/src/Resources:/app/Resources
      - ./Sanctuary/src/Scripts:/app/Scripts
    environment: &common-env
      - Database__Provider=0
      - Database__VersionString=10.5.29-mariadb
      - Database__ConnectionString=Server=sanctuary.mysql;Port=3306;Database=sanctuary;User=sanctuary;Password=sanctuary;
      - Server__LoginGatewayAddress=sanctuary.login:20041
      - Server__ServerAddress=127.0.0.1:20260
      - Urls=http://0.0.0.0:20040
      - WebAPI__LaunchArguments=AssetDelivery:IndirectServerAddress=http://127.0.0.1:20050/assets Portrait:UploadUrl=http://127.0.0.1:20040/image
    depends_on:
      sanctuary.mysql:
        condition: service_healthy
      sanctuary.assetproxy:
        condition: service_started

  sanctuary.gateway:
    build:
      context: ./Sanctuary/src
      dockerfile: Docker/Sanctuary.Gateway.dockerfile
    restart: unless-stopped
    ports:
      - "127.0.0.1:20260:20260/udp"
    volumes: *common-volumes
    environment: *common-env
    depends_on:
      sanctuary.mysql:
        condition: service_healthy
      sanctuary.login:
        condition: service_started

  sanctuary.login:
    build:
      context: ./Sanctuary/src
      dockerfile: Docker/Sanctuary.Login.dockerfile
    restart: unless-stopped
    ports:
      - "127.0.0.1:20041:20041/udp"
      - "127.0.0.1:20042:20042/udp"
    volumes: *common-volumes
    environment: *common-env
    depends_on:
      sanctuary.mysql:
        condition: service_healthy

volumes:
  db:
""";

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string ErrorMessage => string.IsNullOrWhiteSpace(StandardError) ? $"Process exited with code {ExitCode}." : StandardError.Trim();
    }
}
