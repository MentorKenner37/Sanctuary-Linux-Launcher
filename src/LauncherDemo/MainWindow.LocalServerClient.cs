using Avalonia.Media;
using Avalonia.Threading;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private static readonly Uri OfficialClientBaseUri = new("https://opensourcefreerealms.com/");

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

        File.WriteAllText(Path.Combine(LocalManifestHostDirectory, "clientmanifest.xml"), manifestXml);

        var downloaded = 0;
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
            {
                alreadyCurrent++;
                continue;
            }

            SetLocalServerStatus(
                "DOWNLOADING GAME CLIENT…",
                Muted,
                $"Downloading official client file {i + 1}/{files.Count}: {file.RelativePath}");

            await DownloadClientFileAsync(OfficialClientBaseUri, LocalManifestClientDirectory, file);
            downloaded++;
        }

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
}
