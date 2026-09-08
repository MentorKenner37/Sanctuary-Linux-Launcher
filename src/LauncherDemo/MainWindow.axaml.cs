using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HashDepot;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow : Window
{
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private const int MaxLogoBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#55D27E"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#A0A0A0"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#E06A6A"));

    private readonly HttpClient _httpClient = new()
    {
        Timeout = RequestTimeout
    };

    private Uri? _serverBaseUri;
    private ServerManifest? _serverManifest;
    private Process? _gameProcess;

    public MainWindow()
    {
        InitializeComponent();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Sanctuary-Linux-Launcher", "0.2"));
    }

    private async void ConnectClicked(object? sender, RoutedEventArgs e)
    {
        if (!TryNormalizeServerUrl(ServerUrlBox.Text, out var serverBaseUri, out var error))
        {
            SetConnectionState(error, false);
            return;
        }

        SetBusy(true);
        OnlineText.Text = "CONNECTING";
        OnlineText.Foreground = Muted;
        ConnectionStatusText.Text = "Fetching servermanifest.xml…";
        ConnectionStatusText.Foreground = Muted;
        ClientStatusText.Text = "Waiting for server connection.";

        try
        {
            var manifestUri = new Uri(serverBaseUri, "servermanifest.xml");
            var xml = await DownloadTextLimitedAsync(manifestUri, MaxManifestBytes);
            var manifest = ParseServerManifest(xml);

            if (!Uri.TryCreate(manifest.WebApiUrl, UriKind.Absolute, out var apiUri) || apiUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("This server's WebApiUrl is not HTTPS. Login is blocked.");

            _serverBaseUri = serverBaseUri;
            _serverManifest = manifest;

            ServerNameText.Text = manifest.Name;
            ServerDescriptionText.Text = manifest.Description;
            ManifestVersionText.Text = $"Manifest: v{manifest.Version} (v2 compatible)";
            WebApiText.Text = manifest.WebApiUrl;
            LoginServerText.Text = manifest.LoginServer;

            var loginReachable = await TestLoginServerAsync(manifest.LoginServer);
            if (loginReachable)
            {
                OnlineText.Text = "ONLINE";
                OnlineText.Foreground = Good;
                ClientStatusText.Text = "Server connected. Ready to verify client files.";
                SetConnectionState($"Connected to {manifest.Name}.", true);
                VerifyButton.IsEnabled = true;
                LaunchButton.IsEnabled = true;
            }
            else
            {
                OnlineText.Text = "PARTIAL";
                OnlineText.Foreground = Bad;
                ClientStatusText.Text = "Manifest loaded, but the login server did not accept a TCP connection.";
                SetConnectionState("Server metadata is reachable; login endpoint could not be verified.", false);
            }

            await TryLoadServerLogoAsync(serverBaseUri, manifest.LogoUrl);
        }
        catch (Exception ex)
        {
            _serverBaseUri = null;
            _serverManifest = null;
            OnlineText.Text = "OFFLINE";
            OnlineText.Foreground = Bad;
            SetConnectionState(ex.Message, false);
            ClientStatusText.Text = "Connection failed before the server could be verified.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void VerifyClicked(object? sender, RoutedEventArgs e)
    {
        if (_serverBaseUri is null || _serverManifest is null)
            return;

        SetBusy(true);
        try
        {
            await VerifyAndUpdateClientAsync(_serverBaseUri, _serverManifest);
        }
        catch (Exception ex)
        {
            SetClientProgressText($"Client verification failed: {ex.Message}", Bad);
        }
        finally
        {
            ClientProgress.IsVisible = false;
            SetBusy(false);
        }
    }

    private async void LaunchClicked(object? sender, RoutedEventArgs e)
    {
        if (_serverBaseUri is null || _serverManifest is null)
            return;

        if (_gameProcess is { HasExited: false })
        {
            LaunchStatusText.Text = "Free Realms is already running.";
            LaunchStatusText.Foreground = Bad;
            return;
        }

        var username = UsernameBox.Text?.Trim() ?? string.Empty;
        var password = PasswordBox.Text ?? string.Empty;
        if (username.Length == 0 || password.Length == 0)
        {
            LaunchStatusText.Text = "Enter your username and password first.";
            LaunchStatusText.Foreground = Bad;
            return;
        }

        SetBusy(true);
        LaunchStatusText.Foreground = Muted;

        try
        {
            SetClientProgressText("Checking client files…", Muted);
            var clientDirectory = await VerifyAndUpdateClientAsync(_serverBaseUri, _serverManifest);

            LaunchStatusText.Text = "Signing in…";
            var login = await LoginAsync(_serverManifest, username, password);

            LaunchStatusText.Text = "Starting Proton…";
            LaunchGame(_serverManifest, clientDirectory, login);

            LaunchStatusText.Text = "Free Realms launched.";
            LaunchStatusText.Foreground = Good;
            PasswordBox.Text = string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            LaunchStatusText.Text = "Login failed: username or password was rejected.";
            LaunchStatusText.Foreground = Bad;
        }
        catch (Exception ex)
        {
            LaunchStatusText.Text = $"Launch failed: {ex.Message}";
            LaunchStatusText.Foreground = Bad;
        }
        finally
        {
            ClientProgress.IsVisible = false;
            SetBusy(false);
            if (_gameProcess is { HasExited: false })
                LaunchButton.IsEnabled = false;
        }
    }

    private async Task<string> VerifyAndUpdateClientAsync(Uri serverBaseUri, ServerManifest manifest)
    {
        SetClientProgressText("Downloading clientmanifest.xml…", Muted);
        ClientProgress.Value = 0;
        ClientProgress.IsVisible = true;

        var xml = await DownloadTextLimitedAsync(new Uri(serverBaseUri, "clientmanifest.xml"), MaxManifestBytes);
        var clientManifest = ParseClientManifest(xml);

        if (clientManifest.Version != 1)
            throw new InvalidDataException($"Unsupported client manifest version {clientManifest.Version}.");

        if (clientManifest.Languages.Count > 0 && !clientManifest.Languages.Contains("en_US", StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("This server does not advertise en_US client support.");

        var serverDirectory = GetServerDirectory(manifest.Name, serverBaseUri);
        var clientDirectory = Path.Combine(serverDirectory, "Client");
        Directory.CreateDirectory(clientDirectory);

        var files = FlattenClientFiles(clientManifest.RootFolder).ToList();
        if (files.Count == 0)
            throw new InvalidDataException("Client manifest does not contain any files.");

        var needsDownload = new List<ClientFileEntry>();
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            SetClientProgressText($"Checking {i + 1}/{files.Count}: {file.RelativePath}", Muted);
            var localPath = GetSafeClientPath(clientDirectory, file.RelativePath);
            var valid = await IsLocalFileValidAsync(localPath, file);
            if (!valid)
                needsDownload.Add(file);

            ClientProgress.Value = 35.0 * (i + 1) / files.Count;
        }

        if (needsDownload.Count == 0)
        {
            ClientProgress.Value = 100;
            SetClientProgressText("All client files are up to date.", Good);
            return clientDirectory;
        }

        for (var i = 0; i < needsDownload.Count; i++)
        {
            var file = needsDownload[i];
            SetClientProgressText($"Downloading {i + 1}/{needsDownload.Count}: {file.RelativePath}", Muted);
            await DownloadClientFileAsync(serverBaseUri, clientDirectory, file);
            ClientProgress.Value = 35 + 65.0 * (i + 1) / needsDownload.Count;
        }

        ClientProgress.Value = 100;
        SetClientProgressText($"Client ready. Updated {needsDownload.Count} file(s).", Good);
        return clientDirectory;
    }

    private void SetClientProgressText(string message, IBrush brush)
    {
        ClientStatusText.Text = message;
        ClientStatusText.Foreground = brush;
        LaunchStatusText.Text = message;
        LaunchStatusText.Foreground = brush;
    }

    private async Task<LoginResponse> LoginAsync(ServerManifest manifest, string username, string password)
    {
        if (!TryNormalizeHttpsBaseUrl(manifest.WebApiUrl, out var apiBaseUri))
            throw new InvalidDataException("Server WebApiUrl is not a valid HTTPS URL.");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(apiBaseUri, "login"))
        {
            Content = JsonContent.Create(new LoginRequest(username, password))
        };
        using var response = await _httpClient.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Login API returned {(int)response.StatusCode} {response.ReasonPhrase}.");

        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (login is null || string.IsNullOrWhiteSpace(login.SessionId))
            throw new InvalidDataException("Login response did not contain a session ID.");

        return login;
    }

    private void LaunchGame(ServerManifest manifest, string clientDirectory, LoginResponse login)
    {
        var executable = Path.Combine(clientDirectory, "FreeRealms.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("FreeRealms.exe is missing after client verification.", executable);

        var protonPath = ReadRuntimeConfig("proton-path.txt");
        var steamRoot = ReadRuntimeConfig("steam-path.txt");
        var prefixPath = ReadRuntimeConfig("prefix-path.txt");

        if (string.IsNullOrWhiteSpace(protonPath) || !File.Exists(protonPath))
            throw new FileNotFoundException("Configured Proton runtime was not found. Run/repair the Sanctuary Linux Installer.");
        if (string.IsNullOrWhiteSpace(steamRoot) || !Directory.Exists(steamRoot))
            throw new DirectoryNotFoundException("Configured Steam root was not found. Run/repair the Sanctuary Linux Installer.");
        if (string.IsNullOrWhiteSpace(prefixPath))
            prefixPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "OSFR-Linux", "ProtonPrefix");

        Directory.CreateDirectory(prefixPath);

        var tokens = new List<string>
        {
            $"Server={manifest.LoginServer}",
            $"SessionId={login.SessionId}",
            "Internationalization:Locale=en_US"
        };
        if (!string.IsNullOrWhiteSpace(login.LaunchArguments))
            tokens.Add(login.LaunchArguments.Trim());

        var displayPreferences = LoadGameDisplayPreferences();
        var requestedFps = ParseFrameRate(displayPreferences.FrameRate);
        var dxvkLimit = requestedFps == 0 ? 0 : requestedFps;
        var syncInterval = IsVSyncEnabled(displayPreferences.VSync) ? 1 : 0;

        Directory.CreateDirectory(_launcherStateDirectory);
        var dxvkConfigPath = Path.Combine(_launcherStateDirectory, "dxvk-launcher.conf");
        File.WriteAllText(
            dxvkConfigPath,
            $"[FreeRealms.exe]{Environment.NewLine}" +
            $"d3d9.maxFrameRate = {dxvkLimit}{Environment.NewLine}" +
            $"dxgi.maxFrameRate = {dxvkLimit}{Environment.NewLine}" +
            $"d3d9.presentInterval = {syncInterval}{Environment.NewLine}" +
            $"dxgi.syncInterval = {syncInterval}{Environment.NewLine}");

        var gamescopePath = FindExecutableInPath("gamescope");
        var requestedResolution = GetRequestedGameResolution(displayPreferences);
        var primaryResolution = GetPrimaryDisplaySize();
        var borderless = IsBorderlessFullscreen(displayPreferences.DisplayMode);
        var useGamescope = gamescopePath is not null && (borderless || requestedResolution.Width > 0);

        ProcessStartInfo startInfo;
        if (useGamescope)
        {
            var gameWidth = requestedResolution.Width > 0 ? requestedResolution.Width : primaryResolution.Width;
            var gameHeight = requestedResolution.Height > 0 ? requestedResolution.Height : primaryResolution.Height;
            var outputWidth = borderless ? primaryResolution.Width : gameWidth;
            var outputHeight = borderless ? primaryResolution.Height : gameHeight;

            var gameArguments = string.Join(' ', tokens);
            var gamescopeArguments = $"-w {gameWidth} -h {gameHeight} -W {outputWidth} -H {outputHeight} ";
            if (borderless)
                gamescopeArguments += "-f ";

            gamescopeArguments += $"-- \"{protonPath}\" run \"FreeRealms.exe\" {gameArguments}";

            startInfo = new ProcessStartInfo
            {
                FileName = gamescopePath!,
                WorkingDirectory = clientDirectory,
                Arguments = gamescopeArguments,
                UseShellExecute = false
            };
        }
        else
        {
            startInfo = new ProcessStartInfo
            {
                FileName = protonPath,
                WorkingDirectory = clientDirectory,
                Arguments = $"run \"FreeRealms.exe\" {string.Join(' ', tokens)}",
                UseShellExecute = false
            };
        }

        startInfo.Environment["STEAM_COMPAT_DATA_PATH"] = prefixPath;
        startInfo.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = steamRoot;
        startInfo.Environment["PROTON_LOG"] = "0";
        startInfo.Environment["DXVK_CONFIG_FILE"] = dxvkConfigPath;
        // Keep the legacy limiter variable for older Proton/DXVK builds.
        startInfo.Environment["DXVK_FRAME_RATE"] = requestedFps.ToString();

        var backend = ReadRuntimeConfig("graphics-backend.txt");
        startInfo.Environment["PROTON_USE_WINED3D"] = backend.Equals("wined3d", StringComparison.OrdinalIgnoreCase) ? "1" : "0";

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        process.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            process.Dispose();
            if (ReferenceEquals(_gameProcess, process))
                _gameProcess = null;
            LaunchButton.IsEnabled = _serverManifest is not null;
            LaunchStatusText.Text = "Free Realms exited.";
            LaunchStatusText.Foreground = Muted;
        });

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException(useGamescope ? "Gamescope could not be started." : "Proton process could not be started.");
        }

        _gameProcess = process;
    }

    private static ClientManifest ParseClientManifest(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("Client manifest has no root element.");
        if (!root.Name.LocalName.Equals("ClientManifest", StringComparison.Ordinal))
            throw new InvalidDataException("Document is not a Sanctuary client manifest.");
        if (!int.TryParse(root.Attribute("version")?.Value, out var version))
            throw new InvalidDataException("Client manifest version is missing or invalid.");

        var languages = (root.Attribute("languages")?.Value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var folderElement = root.Elements().FirstOrDefault(x => x.Name.LocalName == "Folder")
            ?? throw new InvalidDataException("Client manifest has no root Folder.");

        return new ClientManifest(version, languages, ParseFolder(folderElement));
    }

    private static ClientFolder ParseFolder(XElement element)
    {
        var name = element.Attribute("name")?.Value ?? string.Empty;
        ValidatePathSegment(name, allowEmpty: true);

        var files = new List<ClientFile>();
        foreach (var fileElement in element.Elements().Where(x => x.Name.LocalName == "File"))
        {
            var fileName = fileElement.Attribute("name")?.Value
                ?? throw new InvalidDataException("Client file is missing a name.");
            ValidatePathSegment(fileName, allowEmpty: false);

            if (!uint.TryParse(fileElement.Attribute("size")?.Value, out var size))
                throw new InvalidDataException($"Invalid size for client file {fileName}.");
            if (!ulong.TryParse(fileElement.Attribute("hash")?.Value, out var hash))
                throw new InvalidDataException($"Invalid hash for client file {fileName}.");

            files.Add(new ClientFile(fileName, size, hash));
        }

        var folders = element.Elements()
            .Where(x => x.Name.LocalName == "Folder")
            .Select(ParseFolder)
            .ToList();

        return new ClientFolder(name, files, folders);
    }

    private static IEnumerable<ClientFileEntry> FlattenClientFiles(ClientFolder folder, string path = "")
    {
        foreach (var file in folder.Files)
        {
            var relative = string.IsNullOrEmpty(path) ? file.Name : Path.Combine(path, file.Name);
            yield return new ClientFileEntry(relative, file.Size, file.Hash);
        }

        foreach (var child in folder.Folders)
        {
            var childPath = string.IsNullOrEmpty(path) ? child.Name : Path.Combine(path, child.Name);
            foreach (var file in FlattenClientFiles(child, childPath))
                yield return file;
        }
    }

    private static async Task<bool> IsLocalFileValidAsync(string path, ClientFileEntry file)
    {
        if (!File.Exists(path))
            return false;

        var info = new FileInfo(path);
        if (info.Length != file.Size)
            return false;

        return await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            return XXHash.Hash64(stream) == file.Hash;
        });
    }

    private async Task DownloadClientFileAsync(Uri serverBaseUri, string clientDirectory, ClientFileEntry file)
    {
        var destination = GetSafeClientPath(clientDirectory, file.RelativePath);
        var directory = Path.GetDirectoryName(destination) ?? clientDirectory;
        Directory.CreateDirectory(directory);

        var relativeUrl = "client/" + string.Join('/', file.RelativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(x => x.Length > 0)
            .Select(Uri.EscapeDataString));
        var uri = new Uri(serverBaseUri, relativeUrl);

        var temporary = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long contentLength && contentLength != file.Size)
                throw new InvalidDataException($"Server returned the wrong size for {file.RelativePath}.");

            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > file.Size)
                        throw new InvalidDataException($"Download exceeded declared size for {file.RelativePath}.");
                    await output.WriteAsync(buffer.AsMemory(0, read));
                }
            }

            var valid = await IsLocalFileValidAsync(temporary, file);
            if (!valid)
                throw new InvalidDataException($"Downloaded file failed XXHash64 verification: {file.RelativePath}.");

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string GetServerDirectory(string serverName, Uri serverBaseUri)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var safeName = new string(serverName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "Server";

        var normalizedServerUrl = serverBaseUri.AbsoluteUri.TrimEnd('/').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedServerUrl)))[..12];
        var safeHost = new string(serverBaseUri.Host.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' ? ch : '_').ToArray());
        var folderName = $"{safeName} [{safeHost}] [{hash}]";

        var path = Path.Combine(localAppData, "OSFRLauncher", "Servers", folderName);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetSafeClientPath(string clientDirectory, string relativePath)
    {
        var root = Path.GetFullPath(clientDirectory) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(clientDirectory, relativePath));
        if (!candidate.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidDataException("Client manifest attempted to escape the client directory.");
        return candidate;
    }

    private static void ValidatePathSegment(string value, bool allowEmpty)
    {
        if (allowEmpty && value.Length == 0)
            return;
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Contains('/') || value.Contains('\\') || Path.IsPathRooted(value))
            throw new InvalidDataException($"Unsafe client manifest path segment: '{value}'.");
    }

    private static string ReadRuntimeConfig(string fileName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(home, ".local", "share", "OSFR-Linux", "Launcher", fileName),
            Path.Combine(home, ".local", "share", "OSFR-Linux", fileName)
        };

        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                {
                    var value = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static bool TryNormalizeServerUrl(string? value, out Uri baseUri, out string error)
    {
        baseUri = null!;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Enter a server URL.";
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
        {
            error = "Server URL is not a valid absolute URL.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "Sanctuary Linux Launcher requires HTTPS server URLs.";
            return false;
        }

        var text = parsed.AbsoluteUri.EndsWith('/') ? parsed.AbsoluteUri : parsed.AbsoluteUri + "/";
        baseUri = new Uri(text);
        return true;
    }

    private static bool TryNormalizeHttpsBaseUrl(string value, out Uri baseUri)
    {
        baseUri = null!;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
            return false;
        baseUri = new Uri(parsed.AbsoluteUri.EndsWith('/') ? parsed.AbsoluteUri : parsed.AbsoluteUri + "/");
        return true;
    }

    private async Task<string> DownloadTextLimitedAsync(Uri uri, int maxBytes)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
            throw new InvalidDataException($"Manifest is larger than {maxBytes / 1024} KiB.");

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var memory = new MemoryStream();
        var buffer = new byte[32 * 1024];
        var total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
                break;
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException("Manifest exceeded the size limit.");
            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static ServerManifest ParseServerManifest(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("Manifest has no root element.");

        if (!root.Name.LocalName.Equals("ServerManifest", StringComparison.Ordinal))
            throw new InvalidDataException("Document is not a Sanctuary server manifest.");
        if (!int.TryParse(root.Attribute("version")?.Value, out var version))
            throw new InvalidDataException("Manifest version is missing or invalid.");
        if (version is < 1 or > 2)
            throw new InvalidDataException($"Unsupported server manifest version: {version}. This launcher supports v1 and v2.");

        string Required(string name) =>
            root.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } value
                ? value
                : throw new InvalidDataException($"Manifest is missing required field {name}.");

        string? Optional(string name) =>
            root.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } value
                ? value
                : null;

        return new ServerManifest(version, Required("Name"), Required("Description"), Required("WebApiUrl"), Required("LoginServer"), Optional("LogoUrl"));
    }

    private static async Task<bool> TestLoginServerAsync(string loginServer)
    {
        if (!TryParseHostPort(loginServer, out var host, out var port))
            return false;

        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryParseHostPort(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Port > 0)
        {
            host = uri.Host;
            port = uri.Port;
            return true;
        }

        var split = value.LastIndexOf(':');
        if (split <= 0 || split == value.Length - 1)
            return false;

        host = value[..split].Trim().Trim('[', ']');
        return host.Length > 0 && int.TryParse(value[(split + 1)..], out port) && port is > 0 and <= 65535;
    }

    private async Task TryLoadServerLogoAsync(Uri serverBaseUri, string? logoUrl)
    {
        ServerLogoImage.Source = null;
        ServerLogoImage.IsVisible = false;
        ServerLogoFallback.IsVisible = true;

        var candidates = new List<Uri>();
        if (!string.IsNullOrWhiteSpace(logoUrl) && Uri.TryCreate(serverBaseUri, logoUrl, out var manifestLogo) && manifestLogo.Scheme == Uri.UriSchemeHttps)
            candidates.Add(manifestLogo);
        candidates.Add(new Uri(serverBaseUri, "servericon.png"));

        foreach (var candidate in candidates.Distinct())
        {
            try
            {
                using var response = await _httpClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                    continue;
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (response.Content.Headers.ContentLength is > MaxLogoBytes)
                    continue;

                await using var source = await response.Content.ReadAsStreamAsync();
                using var bounded = new MemoryStream();
                var buffer = new byte[16 * 1024];
                var total = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > MaxLogoBytes)
                        throw new InvalidDataException("Server logo is too large.");
                    bounded.Write(buffer, 0, read);
                }

                bounded.Position = 0;
                ServerLogoImage.Source = new Bitmap(bounded);
                ServerLogoImage.IsVisible = true;
                ServerLogoFallback.IsVisible = false;
                return;
            }
            catch
            {
            }
        }
    }

    private void SetBusy(bool busy)
    {
        ConnectButton.IsEnabled = !busy;
        VerifyButton.IsEnabled = !busy && _serverManifest is not null;
        LaunchButton.IsEnabled = !busy && _serverManifest is not null && _gameProcess is null;
    }

    private void SetConnectionState(string message, bool success)
    {
        ConnectionStatusText.Text = message;
        ConnectionStatusText.Foreground = success ? Good : Bad;
    }

    private sealed record ServerManifest(int Version, string Name, string Description, string WebApiUrl, string LoginServer, string? LogoUrl);
    private sealed record ClientManifest(int Version, List<string> Languages, ClientFolder RootFolder);
    private sealed record ClientFolder(string Name, List<ClientFile> Files, List<ClientFolder> Folders);
    private sealed record ClientFile(string Name, uint Size, ulong Hash);
    private sealed record ClientFileEntry(string RelativePath, uint Size, ulong Hash);
    private sealed record LoginRequest(string Username, string Password);
    private sealed record LoginResponse(string SessionId, string? LaunchArguments);
}
