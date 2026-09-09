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

    private TextBlock? _localServerStatusText;
    private TextBlock? _localServerDetailsText;
    private Button? _localServerSetupButton;
    private Button? _localServerStartButton;
    private Button? _localServerStopButton;
    private bool _localServerTabInitialized;

    private void InitializeLocalServerTab()
    {
        if (_localServerTabInitialized)
            return;
        if (ServersPage.Content is not Control existingServersContent)
            return;

        _localServerTabInitialized = true;

        var tabControl = new TabControl
        {
            Margin = new Thickness(0),
            Items = new object[]
            {
                new TabItem
                {
                    Header = "SAVED SERVERS",
                    Content = existingServersContent
                },
                new TabItem
                {
                    Header = "LOCAL SERVER",
                    Content = BuildLocalServerPanel()
                }
            }
        };

        ServersPage.Content = tabControl;
    }

    private Control BuildLocalServerPanel()
    {
        _localServerStatusText = new TextBlock
        {
            Text = "CHECKING…",
            Foreground = Muted,
            FontWeight = FontWeight.Bold
        };

        _localServerDetailsText = new TextBlock
        {
            Text = "The local server is managed separately from Saved Servers. Add it to Saved Servers yourself when it is ready for client connections.",
            Foreground = new SolidColorBrush(Color.Parse("#9E9E9E")),
            TextWrapping = TextWrapping.Wrap
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
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                _localServerSetupButton,
                _localServerStartButton,
                _localServerStopButton,
                refreshButton
            }
        };

        var panel = new StackPanel
        {
            Margin = new Thickness(34, 30),
            Spacing = 18
        };

        panel.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = "LOCAL SERVER",
                    FontSize = 25,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(Color.Parse("#F5F5F5"))
                },
                new TextBlock
                {
                    Text = "Set up and control a Sanctuary server that runs only on this computer.",
                    Foreground = new SolidColorBrush(Color.Parse("#9E9E9E")),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        });

        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#151515")),
            BorderBrush = new SolidColorBrush(Color.Parse("#303030")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 13,
                Children =
                {
                    new TextBlock { Text = "SERVER STATUS", Foreground = Good, FontWeight = FontWeight.Bold, FontSize = 12 },
                    _localServerStatusText,
                    _localServerDetailsText,
                    buttonRow
                }
            }
        });

        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#151515")),
            BorderBrush = new SolidColorBrush(Color.Parse("#303030")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 9,
                Children =
                {
                    new TextBlock { Text = "LOCAL ENDPOINTS", Foreground = Good, FontWeight = FontWeight.Bold, FontSize = 12 },
                    new TextBlock { Text = "Web API: http://127.0.0.1:20040", Foreground = new SolidColorBrush(Color.Parse("#E8E8E8")) },
                    new TextBlock { Text = "Gateway: 127.0.0.1:20260/UDP", Foreground = new SolidColorBrush(Color.Parse("#E8E8E8")) },
                    new TextBlock { Text = "Login: 127.0.0.1:20041–20042/UDP", Foreground = new SolidColorBrush(Color.Parse("#E8E8E8")) },
                    new TextBlock
                    {
                        Text = "No Join Local button is provided. Once the server has the manifest/client hosting needed by the launcher, add its address through Saved Servers like any other server.",
                        Foreground = new SolidColorBrush(Color.Parse("#747474")),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap
                    },
                    openFolderButton
                }
            }
        });

        return new ScrollViewer { Content = panel };
    }

    private async void LocalServerSetupClicked(object? sender, RoutedEventArgs e)
    {
        SetLocalServerBusy(true);
        SetLocalServerStatus("SETTING UP…", Muted, "Preparing the local Sanctuary server. The first setup can take a while.");

        try
        {
            Directory.CreateDirectory(_localServerRoot);

            if (!CommandExists("git"))
                throw new InvalidOperationException("Git is not installed.");
            if (!CommandExists("docker"))
                throw new InvalidOperationException("Docker is not installed. Install Docker Engine with the Compose plugin first.");

            if (!Directory.Exists(Path.Combine(LocalServerSourceDirectory, ".git")))
            {
                var clone = await RunProcessAsync(
                    "git",
                    _localServerRoot,
                    "clone", "--depth", "1",
                    "https://github.com/Open-Source-Free-Realms/Sanctuary.git",
                    "Sanctuary");
                if (clone.ExitCode != 0)
                    throw new InvalidOperationException("Could not download Sanctuary. " + clone.ErrorMessage);
            }
            else
            {
                var pull = await RunProcessAsync("git", LocalServerSourceDirectory, "pull", "--ff-only");
                if (pull.ExitCode != 0)
                    throw new InvalidOperationException("Could not update Sanctuary. " + pull.ErrorMessage);
            }

            File.WriteAllText(LocalServerComposePath, BuildLocalComposeFile());
            SetLocalServerStatus("READY", Good, "Local server files are prepared. Press START SERVER to build and start the containers.");
        }
        catch (Exception ex)
        {
            SetLocalServerStatus("SETUP FAILED", Bad, ex.Message);
        }
        finally
        {
            SetLocalServerBusy(false);
            await RefreshLocalServerStatusAsync(preserveReadyMessage: true);
        }
    }

    private async void LocalServerStartClicked(object? sender, RoutedEventArgs e)
    {
        SetLocalServerBusy(true);
        SetLocalServerStatus("STARTING…", Muted, "Building and starting the local Sanctuary services.");

        try
        {
            if (!File.Exists(LocalServerComposePath) || !Directory.Exists(LocalServerSourceDirectory))
                throw new InvalidOperationException("Run SET UP SERVER first.");
            if (!CommandExists("docker"))
                throw new InvalidOperationException("Docker is not installed.");

            var result = await RunProcessAsync(
                "docker",
                _localServerRoot,
                "compose", "-f", LocalServerComposePath, "up", "-d", "--build");

            if (result.ExitCode != 0)
                throw new InvalidOperationException("Docker could not start the server. " + result.ErrorMessage);

            await Task.Delay(1500);
            await RefreshLocalServerStatusAsync();
        }
        catch (Exception ex)
        {
            SetLocalServerStatus("START FAILED", Bad, ex.Message);
        }
        finally
        {
            SetLocalServerBusy(false);
        }
    }

    private async void LocalServerStopClicked(object? sender, RoutedEventArgs e)
    {
        SetLocalServerBusy(true);
        SetLocalServerStatus("STOPPING…", Muted, "Stopping the local Sanctuary services. Server data will be kept.");

        try
        {
            if (File.Exists(LocalServerComposePath) && CommandExists("docker"))
            {
                var result = await RunProcessAsync(
                    "docker",
                    _localServerRoot,
                    "compose", "-f", LocalServerComposePath, "down");
                if (result.ExitCode != 0)
                    throw new InvalidOperationException("Docker could not stop the server. " + result.ErrorMessage);
            }

            SetLocalServerStatus("OFFLINE", Muted, "Local server is stopped.");
        }
        catch (Exception ex)
        {
            SetLocalServerStatus("STOP FAILED", Bad, ex.Message);
        }
        finally
        {
            SetLocalServerBusy(false);
            await RefreshLocalServerStatusAsync();
        }
    }

    private void LocalServerOpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_localServerRoot);
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                ArgumentList = { _localServerRoot },
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            SetLocalServerStatus("ERROR", Bad, "Could not open the server folder: " + ex.Message);
        }
    }

    private async Task RefreshLocalServerStatusAsync(bool preserveReadyMessage = false)
    {
        if (_localServerStatusText is null)
            return;

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
            var result = await RunProcessAsync(
                "docker",
                _localServerRoot,
                "compose", "-f", LocalServerComposePath, "ps", "--status", "running", "-q");

            var count = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Length;

            if (count >= 4)
            {
                SetLocalServerStatus("ONLINE", Good, "Sanctuary WebAPI, login, gateway, and database containers are running locally.");
                if (_localServerStopButton is not null) _localServerStopButton.IsEnabled = true;
            }
            else if (count > 0)
            {
                SetLocalServerStatus("PARTIAL", Bad, $"{count}/4 local server containers are running.");
                if (_localServerStopButton is not null) _localServerStopButton.IsEnabled = true;
            }
            else
            {
                if (!preserveReadyMessage)
                    SetLocalServerStatus("OFFLINE", Muted, "Server is set up but not running.");
                if (_localServerStopButton is not null) _localServerStopButton.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            SetLocalServerStatus("STATUS UNKNOWN", Bad, ex.Message);
        }
    }

    private void SetLocalServerBusy(bool busy)
    {
        if (_localServerSetupButton is not null) _localServerSetupButton.IsEnabled = !busy;
        if (_localServerStartButton is not null) _localServerStartButton.IsEnabled = !busy;
        if (_localServerStopButton is not null) _localServerStopButton.IsEnabled = !busy;
    }

    private void SetLocalServerStatus(string status, IBrush brush, string details)
    {
        if (_localServerStatusText is not null)
        {
            _localServerStatusText.Text = status;
            _localServerStatusText.Foreground = brush;
        }

        if (_localServerDetailsText is not null)
            _localServerDetailsText.Text = details;
    }

    private static bool CommandExists(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                ArgumentList = { command },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            process?.WaitForExit(1500);
            return process is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start())
            throw new InvalidOperationException($"Could not start {fileName}.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

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
    depends_on:
      sanctuary.mysql:
        condition: service_healthy

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
        public string ErrorMessage => string.IsNullOrWhiteSpace(StandardError)
            ? $"Process exited with code {ExitCode}."
            : StandardError.Trim();
    }
}
