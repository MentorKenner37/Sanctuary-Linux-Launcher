using System.IO.Compression;
using System.Net;
using System.Xml;
using System.Xml.Linq;
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
    private string LocalAssetPreloadValidPath => Path.Combine(LocalAssetProxyDirectory, "preload-valid-assets.txt");
    private string LocalAssetPreloadMissingPath => Path.Combine(LocalAssetProxyDirectory, "preload-missing-assets.txt");
    private string LocalAssetPreloadFailureLogPath => Path.Combine(LocalAssetProxyDirectory, "preload-failures.log");
    private string LocalAssetPreloadManifestV2MarkerPath => Path.Combine(LocalAssetProxyDirectory, ".preload-kaines-manifest-v2");

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
            Text = "Master list supplies Raising Kaines bucket numbers; Raising Kaines' own Assets_manifest.txt supplies the exact filename (.z included), hash, and size.",
            Foreground = new SolidColorBrush(Color.Parse("#9E9E9E")),
            TextWrapping = TextWrapping.Wrap
        };
        _assetPreloadProgress = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Height = 8, IsVisible = false };
        _assetPreloadButton = new Button { Content = "DISCOVER + PRELOAD ASSETS", Classes = { "primary" } };
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
            var workbookPath = FindMasterAssetWorkbook();
            if (workbookPath is null)
                throw new FileNotFoundException("OSFR Research.xlsx was not found. Put it in the LocalServer/AssetProxy folder, Downloads, Desktop, or Documents, then try again.");

            var kainesManifestPath = FindRaisingKainesAssetManifest();
            if (kainesManifestPath is null)
                throw new FileNotFoundException("Raising Kaines Assets_manifest.txt was not found. Install/update Raising Kaines in OSFR Launcher first so the preloader can use its authoritative .z filenames.");

            SetAssetPreloadStatus("Loading master buckets and Raising Kaines asset manifest…");
            var assets = await Task.Run(() => LoadMasterAssetListFromWorkbook(workbookPath), token);
            var kaines = await Task.Run(() => LoadRaisingKainesManifest(kainesManifestPath), token);

            var requests = assets.Select(asset =>
            {
                var lookupKey = MakeManifestLookupKey(asset.Name, asset.Hash);
                return kaines.TryGetValue(lookupKey, out var manifestAsset)
                    ? new AssetRequest(asset, BuildAssetRequestPath(asset.Key, manifestAsset.FileName, manifestAsset.Hash), manifestAsset.Size, true)
                    : new AssetRequest(asset, BuildAssetRequestPath(asset), asset.Size, false);
            }).ToArray();

            var manifestMatched = requests.Count(r => r.ManifestMatched);
            var manifestCompressed = requests.Count(r => r.ManifestMatched && r.Path.Split('?', 2)[0].EndsWith(".z", StringComparison.OrdinalIgnoreCase));
            var totalBytes = requests.Sum(r => r.DownloadSize);
            SetAssetPreloadStatus($"Loaded {assets.Count:N0} master assets • {manifestMatched:N0} matched Kaines manifest • {manifestCompressed:N0} Kaines .z files • {FormatBytes(totalBytes)}. Loading discovery state…");

            Directory.CreateDirectory(LocalAssetProxyDirectory);

            // v1 trusted spreadsheet column F for compression. That produced false 404s, so discard
            // only the old missing classification once. Known successful paths remain useful.
            if (!File.Exists(LocalAssetPreloadManifestV2MarkerPath))
            {
                if (File.Exists(LocalAssetPreloadMissingPath)) File.Delete(LocalAssetPreloadMissingPath);
                await File.WriteAllTextAsync(LocalAssetPreloadManifestV2MarkerPath,
                    "v2: exact filenames/compression come from Raising Kaines Assets_manifest.txt\n", token);
            }

            var completed = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists(LocalAssetPreloadStatePath)) completed.UnionWith(await File.ReadAllLinesAsync(LocalAssetPreloadStatePath, token));
            if (File.Exists(LocalAssetPreloadValidPath)) completed.UnionWith(await File.ReadAllLinesAsync(LocalAssetPreloadValidPath, token));
            var knownMissing = File.Exists(LocalAssetPreloadMissingPath)
                ? new HashSet<string>(await File.ReadAllLinesAsync(LocalAssetPreloadMissingPath, token), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            var pending = requests.Where(r => !completed.Contains(r.Path) && !knownMissing.Contains(r.Path)).ToArray();
            var knownGood = requests.Count(r => completed.Contains(r.Path));
            var knownGone = requests.Count(r => knownMissing.Contains(r.Path));
            var alreadyClassified = knownGood + knownGone;
            var finished = alreadyClassified;
            var succeeded = knownGood;
            var missing = knownGone;
            var transientFailed = 0;
            long downloadedBytes = 0;
            var sync = new object();
            var firstFailures = new List<string>(12);

            SetAssetPreloadStatus($"Kaines manifest matched {manifestMatched:N0}/{assets.Count:N0}. Discovery: {knownGood:N0} valid • {knownGone:N0} missing • {pending.Length:N0} unknown. Testing with {AssetPreloadConcurrency} workers…");

            await using var stateStream = new FileStream(LocalAssetPreloadStatePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var stateWriter = new StreamWriter(stateStream) { AutoFlush = false };
            await using var validStream = new FileStream(LocalAssetPreloadValidPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var validWriter = new StreamWriter(validStream) { AutoFlush = false };
            await using var missingStream = new FileStream(LocalAssetPreloadMissingPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var missingWriter = new StreamWriter(missingStream) { AutoFlush = false };
            await using var failureStream = new FileStream(LocalAssetPreloadFailureLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var failureWriter = new StreamWriter(failureStream) { AutoFlush = true };
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = AssetPreloadConcurrency, CancellationToken = token }, async (request, cancellationToken) =>
            {
                var ok = false;
                var permanentlyMissing = false;
                string? failureDetail = null;
                try
                {
                    using var response = await client.GetAsync(new Uri(LocalAssetProxyBaseUri, request.Path), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
                    {
                        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                        await body.CopyToAsync(Stream.Null, cancellationToken);
                        ok = true;
                    }
                    else
                    {
                        permanentlyMissing = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone;
                        failureDetail = $"HTTP {(int)response.StatusCode} {response.StatusCode}: {request.Path}";
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    failureDetail = $"{ex.GetType().Name}: {request.Path} — {ex.Message}";
                }

                int current, snapshotSucceeded, snapshotMissing, snapshotTransient;
                long snapshotBytes;
                string[] snapshotFailures;
                lock (sync)
                {
                    finished++;
                    if (ok)
                    {
                        succeeded++;
                        downloadedBytes += request.DownloadSize;
                        stateWriter.WriteLine(request.Path);
                        validWriter.WriteLine(request.Path);
                    }
                    else if (permanentlyMissing)
                    {
                        missing++;
                        missingWriter.WriteLine(request.Path);
                        failureDetail ??= $"Missing: {request.Path}";
                        failureWriter.WriteLine(failureDetail);
                        if (firstFailures.Count < 12) firstFailures.Add(failureDetail);
                    }
                    else
                    {
                        transientFailed++;
                        failureDetail ??= $"Unknown failure: {request.Path}";
                        failureWriter.WriteLine(failureDetail);
                        if (firstFailures.Count < 12) firstFailures.Add(failureDetail);
                    }
                    current = finished;
                    snapshotSucceeded = succeeded;
                    snapshotMissing = missing;
                    snapshotTransient = transientFailed;
                    snapshotBytes = downloadedBytes;
                    snapshotFailures = firstFailures.Take(3).ToArray();
                }

                if (current % 100 == 0 || current == assets.Count || current <= alreadyClassified + 100)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_assetPreloadProgress is not null) _assetPreloadProgress.Value = (double)current / assets.Count;
                        var preview = snapshotFailures.Length == 0 ? string.Empty : "\nExamples unavailable/error:\n" + string.Join("\n", snapshotFailures);
                        SetAssetPreloadStatus($"{current:N0}/{assets.Count:N0} ({(double)current / assets.Count:P1}) • valid: {snapshotSucceeded:N0} • missing: {snapshotMissing:N0} • retryable errors: {snapshotTransient:N0} • cached this run: {FormatBytes(snapshotBytes)}{preview}");
                    });
                }
            });

            await stateWriter.FlushAsync(token);
            await validWriter.FlushAsync(token);
            await missingWriter.FlushAsync(token);
            SetAssetPreloadStatus($"DISCOVERY COMPLETE — {succeeded:N0} available, {missing:N0} confirmed missing, {transientFailed:N0} retryable errors. Requests used Raising Kaines' exact manifest filenames wherever matched.");
            if (_assetPreloadProgress is not null) _assetPreloadProgress.Value = 1;
        }
        catch (OperationCanceledException)
        {
            SetAssetPreloadStatus($"Preload cancelled. Valid/missing discovery progress was kept. Failure log: {LocalAssetPreloadFailureLogPath}");
        }
        catch (Exception ex) { SetAssetPreloadStatus("PRELOAD FAILED — " + ex.Message); }
        finally
        {
            _assetPreloadCancellation.Dispose();
            _assetPreloadCancellation = null;
            if (_assetPreloadButton is not null) _assetPreloadButton.Content = "DISCOVER + PRELOAD ASSETS";
        }
    }

    private static string BuildAssetRequestPath(MasterAsset asset)
    {
        var fileName = asset.Compressed ? asset.Name + ".z" : asset.Name;
        return BuildAssetRequestPath(asset.Key, fileName, asset.Hash);
    }

    private static string BuildAssetRequestPath(int key, string fileName, uint hash) =>
        $"{key:D3}/{EscapeAssetName(fileName)}?{hash}";

    private string? FindRaisingKainesAssetManifest()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var servers = Path.Combine(home, ".local", "share", "OSFRLauncher", "Servers");
        if (!Directory.Exists(servers)) return null;
        return Directory.EnumerateDirectories(servers, "Raising Kaines*", SearchOption.TopDirectoryOnly)
            .Select(dir => Path.Combine(dir, "Client", "Assets_manifest.txt"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static Dictionary<string, KainesManifestAsset> LoadRaisingKainesManifest(string path)
    {
        var result = new Dictionary<string, KainesManifestAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var lastComma = line.LastIndexOf(',');
            if (lastComma <= 0) continue;
            var secondComma = line.LastIndexOf(',', lastComma - 1);
            if (secondComma <= 0) continue;
            var fileName = line[..secondComma];
            if (!uint.TryParse(line[(secondComma + 1)..lastComma], out var hash)) continue;
            if (!long.TryParse(line[(lastComma + 1)..], out var size)) continue;
            var baseName = fileName.EndsWith(".z", StringComparison.OrdinalIgnoreCase) ? fileName[..^2] : fileName;
            result[MakeManifestLookupKey(baseName, hash)] = new KainesManifestAsset(fileName, hash, size);
        }
        if (result.Count == 0) throw new InvalidDataException("Raising Kaines Assets_manifest.txt did not contain readable assets.");
        return result;
    }

    private static string MakeManifestLookupKey(string name, uint hash) => $"{name}\u001f{hash}";

    private string? FindMasterAssetWorkbook()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(LocalAssetProxyDirectory, "OSFR Research.xlsx"), Path.Combine(_localServerRoot, "OSFR Research.xlsx"),
            Path.Combine(home, "Downloads", "OSFR Research.xlsx"), Path.Combine(home, "Desktop", "OSFR Research.xlsx"),
            Path.Combine(home, "Documents", "OSFR Research.xlsx")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static List<MasterAsset> LoadMasterAssetListFromWorkbook(string workbookPath)
    {
        using var archive = ZipFile.OpenRead(workbookPath);
        var sharedStrings = ReadSharedStrings(archive);
        var sheetPath = ResolveWorksheetPath(archive, "Master Asset List");
        var sheetEntry = archive.GetEntry(sheetPath) ?? throw new InvalidDataException($"Worksheet entry {sheetPath} was not found.");
        var assets = new List<MasterAsset>(162651);
        using var stream = sheetEntry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") continue;
            var rowNumber = int.TryParse(reader.GetAttribute("r"), out var parsedRow) ? parsedRow : 0;
            if (rowNumber <= 1) { reader.Skip(); continue; }
            int key = 0; string? name = null; uint hash = 0; long size = 0; var compressed = false;
            using var rowReader = reader.ReadSubtree();
            while (rowReader.Read())
            {
                if (rowReader.NodeType != XmlNodeType.Element || rowReader.LocalName != "c") continue;
                var reference = rowReader.GetAttribute("r") ?? string.Empty;
                var column = new string(reference.TakeWhile(char.IsLetter).ToArray());
                if (column is not ("A" or "B" or "C" or "D" or "F")) { rowReader.Skip(); continue; }
                var cellType = rowReader.GetAttribute("t");
                var value = ReadCellValue(rowReader);
                if (cellType == "s" && int.TryParse(value, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count) value = sharedStrings[sharedIndex];
                switch (column)
                {
                    case "A": if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var kv)) key = (int)kv; break;
                    case "B": name = value; break;
                    case "C": if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hv)) hash = unchecked((uint)hv); break;
                    case "D": if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sv)) size = (long)sv; break;
                    case "F": compressed = value == "1" || value.Equals("TRUE", StringComparison.OrdinalIgnoreCase); break;
                }
            }
            if (!string.IsNullOrWhiteSpace(name)) assets.Add(new MasterAsset(key, name, hash, size, compressed));
        }
        if (assets.Count == 0) throw new InvalidDataException("The Master Asset List worksheet did not contain any assets.");
        return assets;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return new List<string>();
        var strings = new List<string>();
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si") continue;
            using var subtree = reader.ReadSubtree();
            var text = new System.Text.StringBuilder();
            while (subtree.Read()) if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "t") text.Append(subtree.ReadElementContentAsString());
            strings.Add(text.ToString());
        }
        return strings;
    }

    private static string ResolveWorksheetPath(ZipArchive archive, string sheetName)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("Workbook metadata is missing.");
        XDocument workbook; using (var stream = workbookEntry.Open()) workbook = XDocument.Load(stream);
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var sheet = workbook.Descendants(main + "sheet").FirstOrDefault(node => string.Equals((string?)node.Attribute("name"), sheetName, StringComparison.Ordinal)) ?? throw new InvalidDataException($"Worksheet '{sheetName}' was not found.");
        var relationshipId = (string?)sheet.Attribute(relNs + "id") ?? throw new InvalidDataException("Worksheet relationship is missing.");
        var relEntry = archive.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidDataException("Workbook relationships are missing.");
        XDocument relationships; using (var stream = relEntry.Open()) relationships = XDocument.Load(stream);
        XNamespace packageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        var target = relationships.Descendants(packageRel + "Relationship").Where(node => string.Equals((string?)node.Attribute("Id"), relationshipId, StringComparison.Ordinal)).Select(node => (string?)node.Attribute("Target")).FirstOrDefault() ?? throw new InvalidDataException("Worksheet target is missing.");
        return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target.TrimStart('/');
    }

    private static string ReadCellValue(XmlReader cellReader)
    {
        if (cellReader.IsEmptyElement) return string.Empty;
        var depth = cellReader.Depth;
        while (cellReader.Read())
        {
            if (cellReader.NodeType == XmlNodeType.Element && cellReader.LocalName == "v") return cellReader.ReadElementContentAsString();
            if (cellReader.NodeType == XmlNodeType.Element && cellReader.LocalName == "t") return cellReader.ReadElementContentAsString();
            if (cellReader.NodeType == XmlNodeType.EndElement && cellReader.Depth == depth && cellReader.LocalName == "c") break;
        }
        return string.Empty;
    }

    private static string EscapeAssetName(string name) => string.Join('/', name.Split('/').Select(Uri.EscapeDataString));
    private void SetAssetPreloadStatus(string text) { if (_assetPreloadStatusText is not null) _assetPreloadStatusText.Text = text; }
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private sealed record MasterAsset(int Key, string Name, uint Hash, long Size, bool Compressed);
    private sealed record KainesManifestAsset(string FileName, uint Hash, long Size);
    private sealed record AssetRequest(MasterAsset Asset, string Path, long DownloadSize, bool ManifestMatched);
}