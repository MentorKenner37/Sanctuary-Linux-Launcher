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
    private string LocalAssetPreloadFailureLogPath => Path.Combine(LocalAssetProxyDirectory, "preload-failures.log");

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
        if (_assetPreloadProgress is not null)
        {
            _assetPreloadProgress.IsVisible = true;
            _assetPreloadProgress.Value = 0;
        }

        try
        {
            var workbookPath = FindMasterAssetWorkbook();
            if (workbookPath is null)
                throw new FileNotFoundException("OSFR Research.xlsx was not found. Put it in the LocalServer/AssetProxy folder, Downloads, Desktop, or Documents, then try again.");

            SetAssetPreloadStatus($"Loading master asset list from {Path.GetFileName(workbookPath)}…");
            var assets = await Task.Run(() => LoadMasterAssetListFromWorkbook(workbookPath), token);
            var totalBytes = assets.Sum(asset => asset.Size);
            var compressedCount = assets.Count(asset => asset.Compressed);
            var plainCount = assets.Count - compressedCount;

            SetAssetPreloadStatus($"Loaded {assets.Count:N0} assets ({FormatBytes(totalBytes)}) • {compressedCount:N0} compressed • {plainCount:N0} plain. Preparing preload…");

            Directory.CreateDirectory(LocalAssetProxyDirectory);
            var completed = File.Exists(LocalAssetPreloadStatePath)
                ? new HashSet<string>(await File.ReadAllLinesAsync(LocalAssetPreloadStatePath, token), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            var pending = assets
                .Select(asset => new AssetRequest(asset, BuildAssetRequestPath(asset)))
                .Where(request => !completed.Contains(request.Path))
                .ToArray();

            var alreadyDone = assets.Count - pending.Length;
            var finished = alreadyDone;
            var succeeded = alreadyDone;
            var failed = 0;
            long downloadedBytes = 0;
            var sync = new object();
            var firstFailures = new List<string>(12);

            SetAssetPreloadStatus(alreadyDone > 0
                ? $"Resuming at {alreadyDone:N0}/{assets.Count:N0}. Downloading with {AssetPreloadConcurrency} workers…"
                : $"Downloading {assets.Count:N0} assets with {AssetPreloadConcurrency} workers…");

            await using var stateStream = new FileStream(LocalAssetPreloadStatePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var stateWriter = new StreamWriter(stateStream) { AutoFlush = false };
            await using var failureStream = new FileStream(LocalAssetPreloadFailureLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var failureWriter = new StreamWriter(failureStream) { AutoFlush = true };
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            await Parallel.ForEachAsync(pending, new ParallelOptions
            {
                MaxDegreeOfParallelism = AssetPreloadConcurrency,
                CancellationToken = token
            }, async (request, cancellationToken) =>
            {
                var ok = false;
                string? failureDetail = null;
                try
                {
                    using var response = await client.GetAsync(
                        new Uri(LocalAssetProxyBaseUri, request.Path),
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                    if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
                    {
                        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                        await body.CopyToAsync(Stream.Null, cancellationToken);
                        ok = true;
                    }
                    else
                    {
                        failureDetail = $"HTTP {(int)response.StatusCode} {response.StatusCode}: {request.Path}";
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    failureDetail = $"{ex.GetType().Name}: {request.Path} — {ex.Message}";
                }

                int current;
                int snapshotFailed;
                long snapshotBytes;
                string[] snapshotFailures;
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
                        failureDetail ??= $"Unknown failure: {request.Path}";
                        failureWriter.WriteLine(failureDetail);
                        if (firstFailures.Count < 12)
                            firstFailures.Add(failureDetail);
                    }
                    current = finished;
                    snapshotFailed = failed;
                    snapshotBytes = downloadedBytes;
                    snapshotFailures = firstFailures.Take(3).ToArray();
                }

                if (current % 100 == 0 || current == assets.Count || (snapshotFailed > 0 && current <= alreadyDone + 100))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_assetPreloadProgress is not null)
                            _assetPreloadProgress.Value = (double)current / assets.Count;

                        var failurePreview = snapshotFailures.Length == 0
                            ? string.Empty
                            : "\nFirst failures:\n" + string.Join("\n", snapshotFailures);

                        SetAssetPreloadStatus($"{current:N0}/{assets.Count:N0} ({(double)current / assets.Count:P1}) • cached this run: {FormatBytes(snapshotBytes)} • failed: {snapshotFailed:N0}{failurePreview}");
                    });
                }
            });

            await stateWriter.FlushAsync(token);
            SetAssetPreloadStatus(failed == 0
                ? $"PRELOAD COMPLETE — {succeeded:N0}/{assets.Count:N0} assets are marked cached."
                : $"Preload finished — {succeeded:N0}/{assets.Count:N0} cached, {failed:N0} failed. First failures are shown above; full log: {LocalAssetPreloadFailureLogPath}");

            if (_assetPreloadProgress is not null) _assetPreloadProgress.Value = 1;
        }
        catch (OperationCanceledException)
        {
            SetAssetPreloadStatus($"Preload cancelled. Progress was kept. Failure log: {LocalAssetPreloadFailureLogPath}");
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

    private static string BuildAssetRequestPath(MasterAsset asset)
    {
        var escapedName = EscapeAssetName(asset.Name);
        var fileName = asset.Compressed ? escapedName + ".z" : escapedName;
        return $"{asset.Key:D3}/{fileName}?{asset.Hash}";
    }

    private string? FindMasterAssetWorkbook()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(LocalAssetProxyDirectory, "OSFR Research.xlsx"),
            Path.Combine(_localServerRoot, "OSFR Research.xlsx"),
            Path.Combine(home, "Downloads", "OSFR Research.xlsx"),
            Path.Combine(home, "Desktop", "OSFR Research.xlsx"),
            Path.Combine(home, "Documents", "OSFR Research.xlsx")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static List<MasterAsset> LoadMasterAssetListFromWorkbook(string workbookPath)
    {
        using var archive = ZipFile.OpenRead(workbookPath);
        var sharedStrings = ReadSharedStrings(archive);
        var sheetPath = ResolveWorksheetPath(archive, "Master Asset List");
        var sheetEntry = archive.GetEntry(sheetPath)
            ?? throw new InvalidDataException($"Worksheet entry {sheetPath} was not found.");

        var assets = new List<MasterAsset>(162651);
        using var stream = sheetEntry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit
        });

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
                continue;

            var rowNumber = int.TryParse(reader.GetAttribute("r"), out var parsedRow) ? parsedRow : 0;
            if (rowNumber <= 1)
            {
                reader.Skip();
                continue;
            }

            int key = 0;
            string? name = null;
            uint hash = 0;
            long size = 0;
            var compressed = false;

            using var rowReader = reader.ReadSubtree();
            while (rowReader.Read())
            {
                if (rowReader.NodeType != XmlNodeType.Element || rowReader.LocalName != "c")
                    continue;

                var reference = rowReader.GetAttribute("r") ?? string.Empty;
                var column = new string(reference.TakeWhile(char.IsLetter).ToArray());
                if (column is not ("A" or "B" or "C" or "D" or "F"))
                {
                    rowReader.Skip();
                    continue;
                }

                var cellType = rowReader.GetAttribute("t");
                var value = ReadCellValue(rowReader);
                if (cellType == "s" && int.TryParse(value, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
                    value = sharedStrings[sharedIndex];

                switch (column)
                {
                    case "A":
                        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var keyValue))
                            key = (int)keyValue;
                        break;
                    case "B":
                        name = value;
                        break;
                    case "C":
                        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hashValue))
                            hash = unchecked((uint)hashValue);
                        break;
                    case "D":
                        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sizeValue))
                            size = (long)sizeValue;
                        break;
                    case "F":
                        compressed = value == "1" || value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(name))
                assets.Add(new MasterAsset(key, name, hash, size, compressed));
        }

        if (assets.Count == 0)
            throw new InvalidDataException("The Master Asset List worksheet did not contain any assets.");
        return assets;
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return new List<string>();

        var strings = new List<string>();
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit
        });

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si")
                continue;

            using var subtree = reader.ReadSubtree();
            var text = new System.Text.StringBuilder();
            while (subtree.Read())
            {
                if (subtree.NodeType == XmlNodeType.Element && subtree.LocalName == "t")
                    text.Append(subtree.ReadElementContentAsString());
            }
            strings.Add(text.ToString());
        }
        return strings;
    }

    private static string ResolveWorksheetPath(ZipArchive archive, string sheetName)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml")
            ?? throw new InvalidDataException("Workbook metadata is missing.");
        XDocument workbook;
        using (var stream = workbookEntry.Open()) workbook = XDocument.Load(stream);

        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var sheet = workbook.Descendants(main + "sheet")
            .FirstOrDefault(node => string.Equals((string?)node.Attribute("name"), sheetName, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Worksheet '{sheetName}' was not found.");
        var relationshipId = (string?)sheet.Attribute(relNs + "id")
            ?? throw new InvalidDataException("Worksheet relationship is missing.");

        var relEntry = archive.GetEntry("xl/_rels/workbook.xml.rels")
            ?? throw new InvalidDataException("Workbook relationships are missing.");
        XDocument relationships;
        using (var stream = relEntry.Open()) relationships = XDocument.Load(stream);

        XNamespace packageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        var target = relationships.Descendants(packageRel + "Relationship")
            .Where(node => string.Equals((string?)node.Attribute("Id"), relationshipId, StringComparison.Ordinal))
            .Select(node => (string?)node.Attribute("Target"))
            .FirstOrDefault()
            ?? throw new InvalidDataException("Worksheet target is missing.");

        return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target.TrimStart('/');
    }

    private static string ReadCellValue(XmlReader cellReader)
    {
        if (cellReader.IsEmptyElement) return string.Empty;
        var depth = cellReader.Depth;
        while (cellReader.Read())
        {
            if (cellReader.NodeType == XmlNodeType.Element && cellReader.LocalName == "v")
                return cellReader.ReadElementContentAsString();
            if (cellReader.NodeType == XmlNodeType.Element && cellReader.LocalName == "t")
                return cellReader.ReadElementContentAsString();
            if (cellReader.NodeType == XmlNodeType.EndElement && cellReader.Depth == depth && cellReader.LocalName == "c")
                break;
        }
        return string.Empty;
    }

    private static string EscapeAssetName(string name) =>
        string.Join('/', name.Split('/').Select(Uri.EscapeDataString));

    private void SetAssetPreloadStatus(string text)
    {
        if (_assetPreloadStatusText is not null) _assetPreloadStatusText.Text = text;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }

    private sealed record MasterAsset(int Key, string Name, uint Hash, long Size, bool Compressed);
    private sealed record AssetRequest(MasterAsset Asset, string Path);
}
