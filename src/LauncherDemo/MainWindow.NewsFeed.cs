using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private const string SanctuaryRepository = "Open-Source-Free-Realms/Sanctuary";
    private static readonly TimeSpan NewsCacheLifetime = TimeSpan.FromMinutes(15);

    private StackPanel? _majorNewsPanel;
    private StackPanel? _developmentNewsPanel;
    private TextBlock? _newsStatusText;

    private string NewsCachePath => Path.Combine(_launcherStateDirectory, "sanctuary-news-cache.json");

    private Control BuildAutomaticNewsPage()
    {
        var root = new StackPanel { Margin = new Thickness(34, 30), Spacing = 18 };

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(new TextBlock
        {
            Text = "NEWS",
            FontSize = 25,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Automatically filtered from the official Sanctuary GitHub project.",
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        });
        titleRow.Children.Add(heading);

        var refresh = new Button
        {
            Content = "REFRESH",
            Padding = new Thickness(14, 7),
            VerticalAlignment = VerticalAlignment.Top
        };
        refresh.Click += async (_, _) => await RefreshSanctuaryNewsAsync(forceRefresh: true);
        Grid.SetColumn(refresh, 1);
        titleRow.Children.Add(refresh);
        root.Children.Add(titleRow);

        _majorNewsPanel = new StackPanel { Spacing = 10 };
        root.Children.Add(BuildNewsSection(
            "COMING / IN DEVELOPMENT",
            "Player-facing features, restored content and other notable Sanctuary work.",
            _majorNewsPanel));

        _developmentNewsPanel = new StackPanel { Spacing = 7 };
        root.Children.Add(BuildNewsSection(
            "DEVELOPMENT FEED",
            "Smaller fixes, maintenance and behind-the-scenes work.",
            _developmentNewsPanel));

        _newsStatusText = new TextBlock
        {
            Text = "Loading Sanctuary development news…",
            Foreground = Muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        root.Children.Add(_newsStatusText);

        return root;
    }

    private static Border BuildNewsSection(string title, string subtitle, StackPanel content)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = Good
        });
        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11,
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(content);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#151515")),
            BorderBrush = new SolidColorBrush(Color.Parse("#303030")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
            Child = stack
        };
    }

    private async Task RefreshSanctuaryNewsAsync(bool forceRefresh = false)
    {
        if (_majorNewsPanel is null || _developmentNewsPanel is null || _newsStatusText is null)
            return;

        if (!forceRefresh && TryLoadNewsCache(out var cached, requireFresh: true))
        {
            RenderSanctuaryNews(cached);
            _newsStatusText.Text = $"Sanctuary GitHub • cached {cached.FetchedAt.ToLocalTime():g}";
            return;
        }

        _newsStatusText.Text = "Checking Sanctuary GitHub for relevant development activity…";
        _newsStatusText.Foreground = Muted;

        try
        {
            var entries = await FetchSanctuaryNewsAsync();
            var cache = new SanctuaryNewsCache(DateTimeOffset.UtcNow, entries);
            SaveNewsCache(cache);
            RenderSanctuaryNews(cache);
            _newsStatusText.Text = $"Sanctuary GitHub • updated {DateTime.Now:g}";
            _newsStatusText.Foreground = Good;
        }
        catch (Exception ex)
        {
            if (TryLoadNewsCache(out var staleCache, requireFresh: false))
            {
                RenderSanctuaryNews(staleCache);
                _newsStatusText.Text = $"GitHub refresh failed; showing cached news from {staleCache.FetchedAt.ToLocalTime():g}.";
                _newsStatusText.Foreground = Muted;
            }
            else
            {
                _majorNewsPanel.Children.Clear();
                _developmentNewsPanel.Children.Clear();
                _majorNewsPanel.Children.Add(BuildEmptyNewsText("No cached Sanctuary news is available yet."));
                _developmentNewsPanel.Children.Add(BuildEmptyNewsText("Development activity could not be loaded."));
                _newsStatusText.Text = $"Could not load Sanctuary GitHub: {ex.Message}";
                _newsStatusText.Foreground = Bad;
            }
        }
    }

    private async Task<List<SanctuaryNewsEntry>> FetchSanctuaryNewsAsync()
    {
        var api = $"https://api.github.com/repos/{SanctuaryRepository}";
        var openPullsTask = GetGitHubJsonAsync($"{api}/pulls?state=open&sort=updated&direction=desc&per_page=30");
        var closedPullsTask = GetGitHubJsonAsync($"{api}/pulls?state=closed&sort=updated&direction=desc&per_page=30");
        var issuesTask = GetGitHubJsonAsync($"{api}/issues?state=open&sort=updated&direction=desc&per_page=30");
        var commitsTask = GetGitHubJsonAsync($"{api}/commits?per_page=40");

        await Task.WhenAll(openPullsTask, closedPullsTask, issuesTask, commitsTask);

        var entries = new List<SanctuaryNewsEntry>();
        ParsePullRequests(openPullsTask.Result.RootElement, entries, onlyMerged: false);
        ParsePullRequests(closedPullsTask.Result.RootElement, entries, onlyMerged: true);
        ParseIssues(issuesTask.Result.RootElement, entries);
        ParseCommits(commitsTask.Result.RootElement, entries);

        return entries
            .GroupBy(x => NormalizeNewsKey(x.Title), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.Priority).ThenByDescending(x => x.UpdatedAt).First())
            .OrderByDescending(x => x.IsMajor)
            .ThenByDescending(x => x.Priority)
            .ThenByDescending(x => x.UpdatedAt)
            .Take(30)
            .ToList();
    }

    private async Task<JsonDocument> GetGitHubJsonAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static void ParsePullRequests(JsonElement array, List<SanctuaryNewsEntry> output, bool onlyMerged)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in array.EnumerateArray())
        {
            var mergedAt = ReadDate(item, "merged_at");
            if (onlyMerged && mergedAt is null)
                continue;
            if (!onlyMerged && mergedAt is not null)
                continue;

            var title = ReadString(item, "title");
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var body = ReadString(item, "body");
            var updated = mergedAt ?? ReadDate(item, "updated_at") ?? DateTimeOffset.MinValue;
            var stateLabel = mergedAt is not null ? "RECENTLY MERGED" : "IN DEVELOPMENT";
            var score = ScoreNewsImportance(title, body, sourceWeight: 2);
            var isMajor = score >= 4;

            output.Add(new SanctuaryNewsEntry(
                title,
                BuildNewsSummary(body, isMajor ? 240 : 150),
                ReadString(item, "html_url"),
                updated,
                stateLabel,
                isMajor,
                score + (mergedAt is null ? 2 : 1)));
        }
    }

    private static void ParseIssues(JsonElement array, List<SanctuaryNewsEntry> output)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in array.EnumerateArray())
        {
            if (item.TryGetProperty("pull_request", out _))
                continue;

            var title = ReadString(item, "title");
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var body = ReadString(item, "body");
            var score = ScoreNewsImportance(title, body, sourceWeight: 1);
            var isMajor = score >= 5;
            output.Add(new SanctuaryNewsEntry(
                title,
                BuildNewsSummary(body, isMajor ? 220 : 140),
                ReadString(item, "html_url"),
                ReadDate(item, "updated_at") ?? DateTimeOffset.MinValue,
                "PLANNED / TRACKED",
                isMajor,
                score));
        }
    }

    private static void ParseCommits(JsonElement array, List<SanctuaryNewsEntry> output)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in array.EnumerateArray())
        {
            if (!item.TryGetProperty("commit", out var commit))
                continue;

            var message = ReadString(commit, "message");
            if (string.IsNullOrWhiteSpace(message))
                continue;

            var lines = message.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var title = lines[0].Trim();
            var body = lines.Length > 1 ? string.Join(' ', lines.Skip(1)).Trim() : string.Empty;
            var score = ScoreNewsImportance(title, body, sourceWeight: 0);
            var isMajor = score >= 6;

            DateTimeOffset updated = DateTimeOffset.MinValue;
            if (commit.TryGetProperty("committer", out var committer))
                updated = ReadDate(committer, "date") ?? updated;

            output.Add(new SanctuaryNewsEntry(
                title,
                BuildNewsSummary(body, isMajor ? 180 : 120),
                ReadString(item, "html_url"),
                updated,
                "COMMIT",
                isMajor,
                score - 1));
        }
    }

    private static int ScoreNewsImportance(string title, string body, int sourceWeight)
    {
        var text = $" {title} {body} ".ToLowerInvariant();
        var score = sourceWeight;

        string[] major =
        {
            "feature", "implement", "restore", "restored", "return", "returning", "reintroduc", "bring back",
            "new ", "add ", "added ", "support", "gameplay", "quest", "combat", "housing", "guild", "minigame",
            "mini-game", "zone", "world", "npc", "character", "inventory", "mount", "pet", "collection", "trading",
            "friends", "party", "social", "job", "event", "festival", "card", "tcg", "race", "matchmaking",
            "creation", "customization", "server browser", "login", "multiplayer"
        };
        string[] minor =
        {
            "typo", "refactor", "cleanup", "chore", "ci", "dependency", "dependencies", "deps", "logging", "test",
            "format", "lint", "docs", "readme", "rename", "warning", "build fix", "bump ", "update package"
        };

        foreach (var keyword in major)
            if (text.Contains(keyword, StringComparison.Ordinal))
                score += keyword.Length >= 7 ? 2 : 1;

        foreach (var keyword in minor)
            if (text.Contains(keyword, StringComparison.Ordinal))
                score -= 3;

        if (title.StartsWith("fix", StringComparison.OrdinalIgnoreCase) || title.StartsWith("bug", StringComparison.OrdinalIgnoreCase))
            score -= 2;

        return score;
    }

    private void RenderSanctuaryNews(SanctuaryNewsCache cache)
    {
        if (_majorNewsPanel is null || _developmentNewsPanel is null)
            return;

        _majorNewsPanel.Children.Clear();
        _developmentNewsPanel.Children.Clear();

        var major = cache.Entries.Where(x => x.IsMajor).Take(8).ToList();
        var minor = cache.Entries.Where(x => !x.IsMajor).Take(12).ToList();

        if (major.Count == 0)
            _majorNewsPanel.Children.Add(BuildEmptyNewsText("No major player-facing work was detected in the recent GitHub activity."));
        else
            foreach (var entry in major)
                _majorNewsPanel.Children.Add(BuildMajorNewsCard(entry));

        if (minor.Count == 0)
            _developmentNewsPanel.Children.Add(BuildEmptyNewsText("No smaller development updates were found."));
        else
            foreach (var entry in minor)
                _developmentNewsPanel.Children.Add(BuildDevelopmentNewsRow(entry));
    }

    private static Control BuildMajorNewsCard(SanctuaryNewsEntry entry)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock
        {
            Text = entry.SourceLabel,
            Foreground = Good,
            FontSize = 10,
            FontWeight = FontWeight.Bold
        });
        stack.Children.Add(new TextBlock
        {
            Text = entry.Title,
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(entry.Summary))
            stack.Children.Add(new TextBlock
            {
                Text = entry.Summary,
                Foreground = Brushes.LightGray,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
        stack.Children.Add(BuildNewsFooter(entry));

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#191919")),
            BorderBrush = new SolidColorBrush(Color.Parse("#333333")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = stack
        };
    }

    private static Control BuildDevelopmentNewsRow(SanctuaryNewsEntry entry)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = entry.Title,
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(new TextBlock
        {
            Text = $"{entry.SourceLabel} • {entry.UpdatedAt.ToLocalTime():MMM d, yyyy}",
            Foreground = Muted,
            FontSize = 10
        });
        grid.Children.Add(text);

        if (!string.IsNullOrWhiteSpace(entry.Url))
        {
            var open = new Button { Content = "VIEW", Padding = new Thickness(10, 4), FontSize = 10 };
            open.Click += (_, _) => OpenExternalUrl(entry.Url);
            Grid.SetColumn(open, 1);
            grid.Children.Add(open);
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#191919")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2D2D2D")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 9),
            Child = grid
        };
    }

    private static Control BuildNewsFooter(SanctuaryNewsEntry entry)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock
        {
            Text = entry.UpdatedAt == DateTimeOffset.MinValue ? string.Empty : entry.UpdatedAt.ToLocalTime().ToString("MMM d, yyyy"),
            Foreground = Muted,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        });

        if (!string.IsNullOrWhiteSpace(entry.Url))
        {
            var open = new Button { Content = "VIEW ON GITHUB", Padding = new Thickness(10, 4), FontSize = 10 };
            open.Click += (_, _) => OpenExternalUrl(entry.Url);
            Grid.SetColumn(open, 1);
            row.Children.Add(open);
        }
        return row;
    }

    private static TextBlock BuildEmptyNewsText(string text) => new()
    {
        Text = text,
        Foreground = Muted,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 4)
    };

    private static void OpenExternalUrl(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "xdg-open",
                ArgumentList = { uri.AbsoluteUri },
                UseShellExecute = false
            });
        }
        catch
        {
        }
    }

    private bool TryLoadNewsCache(out SanctuaryNewsCache cache, bool requireFresh)
    {
        cache = new SanctuaryNewsCache(DateTimeOffset.MinValue, new List<SanctuaryNewsEntry>());
        try
        {
            if (!File.Exists(NewsCachePath))
                return false;
            cache = JsonSerializer.Deserialize<SanctuaryNewsCache>(File.ReadAllText(NewsCachePath))
                ?? cache;
            if (cache.Entries.Count == 0)
                return false;
            return !requireFresh || DateTimeOffset.UtcNow - cache.FetchedAt <= NewsCacheLifetime;
        }
        catch
        {
            return false;
        }
    }

    private void SaveNewsCache(SanctuaryNewsCache cache)
    {
        try
        {
            Directory.CreateDirectory(_launcherStateDirectory);
            File.WriteAllText(NewsCachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private static string BuildNewsSummary(string body, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        var text = body.Replace("\r", " ").Replace("\n", " ");
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^\)]*\)", " ");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^\)]*\)", "$1");
        text = Regex.Replace(text, @"[`#>*_~|-]", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length <= maxLength)
            return text;
        return text[..maxLength].TrimEnd() + "…";
    }

    private static string NormalizeNewsKey(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static string ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return DateTimeOffset.TryParse(value.GetString(), out var result) ? result : null;
    }

    public sealed record SanctuaryNewsEntry(
        string Title,
        string Summary,
        string Url,
        DateTimeOffset UpdatedAt,
        string SourceLabel,
        bool IsMajor,
        int Priority);

    public sealed record SanctuaryNewsCache(DateTimeOffset FetchedAt, List<SanctuaryNewsEntry> Entries);
}
