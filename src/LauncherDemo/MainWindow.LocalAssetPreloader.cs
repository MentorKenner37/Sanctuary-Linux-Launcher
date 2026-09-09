using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private const int AssetPreloadConcurrency = 32;
    private static readonly Uri LocalAssetProxyBaseUri = new("http://127.0.0.1:20050/assets/");

    private bool _localAssetPreloaderUiInitialized;
    private Button? _assetPreloadButton;
    private TextBlock? _assetPreloadStatusText;
    private ProgressBar? _assetPreloadProgress;
    private CancellationTokenSource? _assetPreloadCancellation;

    private string LocalAssetPreloadStatePath => Path.Combine(LocalAssetProxyDirectory, "preload-complete.txt");

    private void InitializeLocalAssetPreloaderUi()
    {
        if (_localAssetPreloaderUiInitialized) return;
        if (ServersPage.Content is not TabControl tabs || tabs.ItemsSource is not IEnumerable<object> items) return;

        var localTab = items.OfType<TabItem>()
            .FirstOrDefault(item => string.Equals(item.Header?.ToString(), "LOCAL SERVER", StringComparison.Ordinal));
        if (localTab?.Content is not ScrollViewer { Content: StackPanel panel }) return;

        _localAssetPreloaderUiInitialized = true;
        _assetPreloadStatusText = new TextBlock
        {
            Text = "Master list: 162,651 assets (~4.10 GB). Preload them through the local nginx cache so the game does not have to discover them while you play.",
            Foreground = new SolidColorBrush(Color.Parse("#9E9E9E")),
            TextWrapping = TextWrapping.Wrap
        };
        _assetPreloadProgress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Height = 8,
            IsVisible = false
        };
        _assetPreloadButton = new Button { Content = "PRELOAD ALL ASSETS", Classes = { "primary" } };
        _assetPreloadButton.Click += LocalAssetPreloadClicked;

        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#151515")),
            BorderBrush = new SolidColorBrush(Color.Parse("#303030")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20),
            Child = new StackPanel
            {
                Spacing = 11,
                Children =
                {
                    new TextBlock { Text = "ASSET PRELOADER", Foreground = Good, FontWeight = FontWeight.Bold, FontSize = 12 },
                    _assetPreloadStatusText,
                    _assetPreloadProgress,
                    _assetPreloadButton
                }
            }
        };

        panel.Children.Insert(Math.Min(1, panel.Children.Count), card);
    }

    private async void LocalAssetPreloadClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_assetPreloadCancellation is not null)
        {
            _assetPreloadCancellation.Cancel();
            if (_assetPreloadButton is not null) _assetPreloadButton.Content = "CANCELLING…";
            return;
        }

        _assetPreloadCancellation = new CancellationTokenSource();
        var token = _assetPreloadCancellation.Token;
        if (_assetPreloadButton is not null) _assetPreloadButton.Content = "CANCEL PRELOAD";
        if (_assetPreloadProgress is not null) { _assetPreloadProgress.IsVisible = true; _assetPreloadProgress.Value = 0; }

        try
        {
            SetAssetPreloadStatus("Loading bundled master asset list…");
            var assets = await Task.Run(LoadBundledMasterAssetList, token);
            var totalBytes = assets.Sum(asset => asset.Size);

            SetAssetPreloadStatus($"Loaded {assets.Count:N0} assets ({FormatBytes(totalBytes)}). Detecting Raising Kaines asset URL format…");
            var pathBuilder = await DiscoverAssetPathBuilderAsync(assets, token);
            if (pathBuilder is null)
                throw new InvalidOperationException("Could not determine the Raising Kaines asset URL format. Make sure the asset proxy is running on port 20050.");

            Directory.CreateDirectory(LocalAssetProxyDirectory);
            var completed = File.Exists(LocalAssetPreloadStatePath)
                ? new HashSet<string>(await File.ReadAllLinesAsync(LocalAssetPreloadStatePath, token), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            var pending = assets
                .Select(asset => new AssetRequest(asset, pathBuilder(asset)))
                .Where(request => !completed.Contains(request.Path))
                .ToArray();

            var alreadyDone = assets.Count - pending.Length;
            var finished = alreadyDone;
            var succeeded = alreadyDone;
            var failed = 0;
            long downloadedBytes = 0;
            var sync = new object();

            SetAssetPreloadStatus(alreadyDone > 0
                ? $"Resuming at {alreadyDone:N0}/{assets.Count:N0}. Downloading with {AssetPreloadConcurrency} workers…"
                : $"Downloading {assets.Count:N0} assets with {AssetPreloadConcurrency} workers…");

            await using var stateStream = new FileStream(LocalAssetPreloadStatePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var stateWriter = new StreamWriter(stateStream) { AutoFlush = false };

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            await Parallel.ForEachAsync(pending, new ParallelOptions
            {
                MaxDegreeOfParallelism = AssetPreloadConcurrency,
                CancellationToken = token
            }, async (request, cancellationToken) =>
            {
                var ok = false;
                try
                {
                    using var response = await client.GetAsync(new Uri(LocalAssetProxyBaseUri, request.Path), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
                    {
                        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                        await body.CopyToAsync(Stream.Null, cancellationToken);
                        ok = true;
                    }
                }
                catch when (!cancellationToken.IsCancellationRequested) { }

                int current;
                int snapshotSucceeded;
                int snapshotFailed;
                long snapshotBytes;
                lock (sync)
                {
                    finished++;
                    if (ok)
                    {
                        succeeded++;
                        downloadedBytes += request.Asset.Size;
                        stateWriter.WriteLine(request.Path);
                    }
                    else
                    {
                        failed++;
                    }
                    current = finished;
                    snapshotSucceeded = succeeded;
                    snapshotFailed = failed;
                    snapshotBytes = downloadedBytes;
                }

                if (current % 250 == 0 || current == assets.Count)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_assetPreloadProgress is not null) _assetPreloadProgress.Value = (double)current / assets.Count;
                        SetAssetPreloadStatus($"{current:N0}/{assets.Count:N0} ({(double)current / assets.Count:P1}) • cached this run: {FormatBytes(snapshotBytes)} • failed: {snapshotFailed:N0}");
                    });
                }
            });

            await stateWriter.FlushAsync(token);
            SetAssetPreloadStatus(failed == 0
                ? $"PRELOAD COMPLETE — {succeeded:N0}/{assets.Count:N0} assets are marked cached."
                : $"Preload finished — {succeeded:N0}/{assets.Count:N0} cached, {failed:N0} failed. Press PRELOAD ALL ASSETS again to retry failures.");
            if (_assetPreloadProgress is not null) _assetPreloadProgress.Value = 1;
        }
        catch (OperationCanceledException)
        {
            SetAssetPreloadStatus("Preload cancelled. Progress was kept; press PRELOAD ALL ASSETS to resume.");
        }
        catch (Exception ex)
        {
            SetAssetPreloadStatus("PRELOAD FAILED — " + ex.Message);
        }
        finally
        {
            _assetPreloadCancellation.Dispose();
            _assetPreloadCancellation = null;
            if (_assetPreloadButton is not null) _assetPreloadButton.Content = "PRELOAD ALL ASSETS";
        }
    }

    private async Task<Func<MasterAsset, string>?> DiscoverAssetPathBuilderAsync(IReadOnlyList<MasterAsset> assets, CancellationToken token)
    {
        var probes = assets.Where(asset => asset.Size is > 0 and <= 128 * 1024).Take(6).ToArray();
        if (probes.Length == 0) return null;

        var candidates = new Func<MasterAsset, string>[]
        {
            asset => EscapeAssetName(asset.Name),
            asset => $"{asset.Key}/{EscapeAssetName(asset.Name)}",
            asset => asset.Hash.ToString(),
            asset => $"{asset.Key}/{asset.Hash}",
            asset => asset.Hash + Uri.EscapeDataString(Path.GetExtension(asset.Name)),
            asset => $"{asset.Key}/{asset.Hash}{Uri.EscapeDataString(Path.GetExtension(asset.Name))}",
            asset => $"{asset.Hash}/{EscapeAssetName(asset.Name)}",
            asset => $"{asset.Key}/{asset.Hash}/{EscapeAssetName(asset.Name)}"
        };

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        foreach (var candidate in candidates)
        {
            var successes = 0;
            foreach (var asset in probes.Take(3))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var response = await client.GetAsync(new Uri(LocalAssetProxyBaseUri, candidate(asset)), HttpCompletionOption.ResponseHeadersRead, token);
                    if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent)) continue;
                    var length = response.Content.Headers.ContentLength;
                    await using var stream = await response.Content.ReadAsStreamAsync(token);
                    await stream.CopyToAsync(Stream.Null, token);
                    if (length is null || asset.Size <= 0 || length == asset.Size) successes++;
                }
                catch when (!token.IsCancellationRequested) { }
            }

            if (successes >= 2) return candidate;
        }

        return null;
    }

    private static List<MasterAsset> LoadBundledMasterAssetList()
    {
        var assetsDirectory = Path.Combine(AppContext.BaseDirectory, "Assets");
        var chunkPaths = Directory.Exists(assetsDirectory)
            ? Directory.GetFiles(assetsDirectory, "master-assets-*.tsv.gz.b64").OrderBy(path => path, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        if (chunkPaths.Length == 0)
            throw new FileNotFoundException("Bundled master asset list was not found in the launcher Assets directory.");

        var assets = new List<MasterAsset>(162651);
        foreach (var chunkPath in chunkPaths)
        {
            var packed = Convert.FromBase64String(File.ReadAllText(chunkPath).Trim());
            using var memory = new MemoryStream(packed, writable: false);
            using var gzip = new GZipStream(memory, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            _ = reader.ReadLine();
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 4 || !int.TryParse(parts[0], out var key) || !uint.TryParse(parts[2], out var hash) || !long.TryParse(parts[3], out var size)) continue;
                assets.Add(new MasterAsset(key, parts[1], hash, size));
            }
        }
        return assets;
    }

    private static string EscapeAssetName(string name) => string.Join('/', name.Split('/').Select(Uri.EscapeDataString));

    private void SetAssetPreloadStatus(string text)
    {
        if (_assetPreloadStatusText is not null) _assetPreloadStatusText.Text = text;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private sealed record MasterAsset(int Key, string Name, uint Hash, long Size);
    private sealed record AssetRequest(MasterAsset Asset, string Path);
}
