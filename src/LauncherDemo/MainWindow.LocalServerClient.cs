using System.Xml.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using HashDepot;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private static readonly Uri OfficialClientBaseUri = new("https://opensourcefreerealms.com/");
    private const int LocalClientDownloadConcurrency = 30;

    private string LocalManifestHostDirectory => Path.Combine(_localServerRoot, "ManifestHost");
    private string LocalManifestClientDirectory => Path.Combine(LocalManifestHostDirectory, "client");
    private string LocalClientReadyMarker => Path.Combine(LocalManifestHostDirectory, ".client-ready");

    private DispatcherTimer? _localServerAutomationTimer;
    private bool _localServerAutomationRunning;

    private void StartLocalServerAutomationMonitor()
    {
        if (_localServerAutomationTimer is not null)
            return;

        _localServerAutomationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _localServerAutomationTimer.Tick += async (_, _) => await TryRunLocalServerAutomationAsync();
        _localServerAutomationTimer.Start();
        _ = TryRunLocalServerAutomationAsync();
    }

    private async Task TryRunLocalServerAutomationAsync()
    {
        if (_localServerAutomationRunning)
            return;
        if (!Directory.Exists(LocalServerSourceDirectory) || !File.Exists(LocalServerComposePath))
            return;
        if (File.Exists(LocalClientReadyMarker))
            return;

        _localServerAutomationRunning = true;
        try
        {
            PatchLocalServerDockerSdk();
            await PrepareLocalClientMirrorAsync();
        }
        catch (Exception ex)
        {
            SetLocalServerStatus("CLIENT SETUP FAILED", Bad, ex.Message);
        }
        finally
        {
            _localServerAutomationRunning = false;
        }
    }

    private void PatchLocalServerDockerSdk()
    {
        var dockerDirectory = Path.Combine(LocalServerSourceDirectory, "src", "Docker");
        if (!Directory.Exists(dockerDirectory))
            return;

        foreach (var dockerfile in Directory.EnumerateFiles(dockerDirectory, "*.dockerfile", SearchOption.TopDirectoryOnly))
        {
            var original = File.ReadAllText(dockerfile);
            var patched = original.Replace(
                "mcr.microsoft.com/dotnet/sdk:9.0",
                "mcr.microsoft.com/dotnet/sdk:10.0",
                StringComparison.Ordinal);

            if (!string.Equals(original, patched, StringComparison.Ordinal))
                File.WriteAllText(dockerfile, patched);
        }
    }

    private async Task PrepareLocalClientMirrorAsync()
    {
        Directory.CreateDirectory(LocalManifestHostDirectory);
        Directory.CreateDirectory(LocalManifestClientDirectory);

        if (File.Exists(LocalClientReadyMarker))
            File.Delete(LocalClientReadyMarker);

        SetLocalServerStatus(
            "DOWNLOADING CLIENT MANIFEST…",
            Muted,
            "Getting the official client manifest from Open Source Free Realms.");

        var manifestXml = await DownloadTextLimitedAsync(
            new Uri(OfficialClientBaseUri, "clientmanifest.xml"),
            MaxManifestBytes);

        var manifest = ParseClientManifest(manifestXml);
        if (manifest.Version != 1)
            throw new InvalidDataException($"Official client manifest version {manifest.Version} is not supported.");

        var files = FlattenClientFiles(manifest.RootFolder).ToList();
        if (files.Count == 0)
            throw new InvalidDataException("The official client manifest does not contain any files.");

        var needsDownload = new List<ClientFileEntry>();
        var alreadyCurrent = 0;

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var localPath = GetSafeClientPath(LocalManifestClientDirectory, file.RelativePath);

            SetLocalServerStatus(
                "PREPARING GAME CLIENT…",
                Muted,
                $"Checking official client file {i + 1}/{files.Count}: {file.RelativePath}");

            if (await IsLocalFileValidAsync(localPath, file))
                alreadyCurrent++;
            else
                needsDownload.Add(file);
        }

        var downloaded = 0;
        if (needsDownload.Count > 0)
        {
            SetLocalServerStatus(
                "DOWNLOADING GAME CLIENT…",
                Muted,
                $"Downloading {needsDownload.Count} client files with up to {LocalClientDownloadConcurrency} simultaneous downloads.");

            using var gate = new SemaphoreSlim(LocalClientDownloadConcurrency);
            var completed = 0;

            var tasks = needsDownload.Select(async file =>
            {
                await gate.WaitAsync();
                try
                {
                    // Upstream can briefly serve a newer file while clientmanifest.xml still has
                    // the old size/hash. Mirror the complete current bytes, then rebuild our local
                    // manifest from those bytes after every download finishes.
                    await DownloadLocalMirrorFileAsync(file);
                    Interlocked.Increment(ref downloaded);
                    var done = Interlocked.Increment(ref completed);

                    Dispatcher.UIThread.Post(() => SetLocalServerStatus(
                        "DOWNLOADING GAME CLIENT…",
                        Muted,
                        $"Downloaded {done}/{needsDownload.Count} needed files • {alreadyCurrent} already current • {LocalClientDownloadConcurrency} parallel connections"));
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);
        }

        SetLocalServerStatus(
            "BUILDING LOCAL CLIENT MANIFEST…",
            Muted,
            "Rebuilding file sizes and XXHash64 values from the mirrored client.");

        var localManifestXml = await Task.Run(() => BuildLocalClientManifest(manifestXml));
        File.WriteAllText(Path.Combine(LocalManifestHostDirectory, "clientmanifest.xml"), localManifestXml);

        try
        {
            var icon = await _httpClient.GetByteArrayAsync(new Uri(OfficialClientBaseUri, "servericon.png"));
            if (icon.Length > 0 && icon.Length <= MaxLogoBytes)
                await File.WriteAllBytesAsync(Path.Combine(LocalManifestHostDirectory, "servericon.png"), icon);
        }
        catch
        {
            // Branding is optional and must not block local-server setup.
        }

        File.WriteAllText(LocalClientReadyMarker, DateTimeOffset.UtcNow.ToString("O"));

        SetLocalServerStatus(
            "CLIENT READY",
            Good,
            $"Official client mirror is ready: {downloaded} downloaded, {alreadyCurrent} already current, {files.Count} total files.");
    }

    private async Task DownloadLocalMirrorFileAsync(ClientFileEntry file)
    {
        var destination = GetSafeClientPath(LocalManifestClientDirectory, file.RelativePath);
        var directory = Path.GetDirectoryName(destination) ?? LocalManifestClientDirectory;
        Directory.CreateDirectory(directory);

        var relativeUrl = "client/" + string.Join('/', file.RelativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(x => x.Length > 0)
            .Select(Uri.EscapeDataString));
        var uri = new Uri(OfficialClientBaseUri, relativeUrl);

        var temporary = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

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
                    if (total > uint.MaxValue)
                        throw new InvalidDataException($"Client file is too large for manifest v1: {file.RelativePath}.");

                    await output.WriteAsync(buffer.AsMemory(0, read));
                }
            }

            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private string BuildLocalClientManifest(string upstreamManifestXml)
    {
        var document = XDocument.Parse(upstreamManifestXml, LoadOptions.None);
        var root = document.Root ?? throw new InvalidDataException("Client manifest has no root element.");
        var rootFolder = root.Elements().FirstOrDefault(x => x.Name.LocalName == "Folder")
            ?? throw new InvalidDataException("Client manifest has no root Folder.");

        RewriteFolderMetadata(rootFolder, string.Empty);
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private void RewriteFolderMetadata(XElement folder, string parentPath)
    {
        var folderName = folder.Attribute("name")?.Value ?? string.Empty;
        var currentPath = string.IsNullOrEmpty(folderName)
            ? parentPath
            : string.IsNullOrEmpty(parentPath) ? folderName : Path.Combine(parentPath, folderName);

        foreach (var fileElement in folder.Elements().Where(x => x.Name.LocalName == "File"))
        {
            var fileName = fileElement.Attribute("name")?.Value
                ?? throw new InvalidDataException("Client file is missing a name.");
            var relativePath = string.IsNullOrEmpty(currentPath)
                ? fileName
                : Path.Combine(currentPath, fileName);
            var localPath = GetSafeClientPath(LocalManifestClientDirectory, relativePath);

            if (!File.Exists(localPath))
                throw new FileNotFoundException("Mirrored client file is missing while rebuilding the local manifest.", localPath);

            var info = new FileInfo(localPath);
            if (info.Length > uint.MaxValue)
                throw new InvalidDataException($"Client file is too large for manifest v1: {relativePath}.");

            ulong hash;
            using (var stream = File.OpenRead(localPath))
                hash = XXHash.Hash64(stream);

            fileElement.SetAttributeValue("size", ((uint)info.Length).ToString());
            fileElement.SetAttributeValue("hash", hash.ToString());
        }

        foreach (var child in folder.Elements().Where(x => x.Name.LocalName == "Folder"))
            RewriteFolderMetadata(child, currentPath);
    }
}
