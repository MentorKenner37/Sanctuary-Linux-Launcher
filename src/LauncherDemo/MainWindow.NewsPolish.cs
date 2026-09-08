using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private bool _newsPolishAttached;
    private readonly HashSet<TextBlock> _polishedNewsTitles = new();

    private void AttachNewsPolish()
    {
        if (_newsPolishAttached)
            return;

        _newsPolishAttached = true;

        void Polish()
        {
            if (_majorNewsPanel is not null)
            {
                _majorNewsPanel.Spacing = 8;
                foreach (var border in _majorNewsPanel.Children.OfType<Border>())
                {
                    border.Background = new SolidColorBrush(Color.Parse("#181B19"));
                    border.BorderBrush = new SolidColorBrush(Color.Parse("#31533C"));
                    border.BorderThickness = new Thickness(3, 1, 1, 1);
                    border.CornerRadius = new CornerRadius(10);
                    border.Padding = new Thickness(15, 12);

                    var texts = border.GetVisualDescendants().OfType<TextBlock>().ToArray();
                    if (texts.Length > 0)
                    {
                        texts[0].FontSize = 9;
                        texts[0].Foreground = Good;
                    }
                    if (texts.Length > 1)
                    {
                        texts[1].FontSize = 14;
                        texts[1].FontWeight = FontWeight.SemiBold;
                        PolishNewsTitle(texts[1]);
                    }
                }
            }

            if (_developmentNewsPanel is not null)
            {
                _developmentNewsPanel.Spacing = 5;
                foreach (var border in _developmentNewsPanel.Children.OfType<Border>())
                {
                    border.Background = new SolidColorBrush(Color.Parse("#171717"));
                    border.BorderBrush = new SolidColorBrush(Color.Parse("#292929"));
                    border.BorderThickness = new Thickness(1);
                    border.CornerRadius = new CornerRadius(7);
                    border.Padding = new Thickness(11, 7);

                    var title = border.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
                    if (title is not null)
                        PolishNewsTitle(title);

                    foreach (var button in border.GetVisualDescendants().OfType<Button>())
                    {
                        button.Padding = new Thickness(9, 3);
                        button.FontSize = 9;
                        button.MinHeight = 0;
                    }
                }
            }

            if (HomePage.Content is Control newsRoot)
            {
                foreach (var text in newsRoot.GetVisualDescendants().OfType<TextBlock>())
                {
                    if (text.Text is "COMING / IN DEVELOPMENT" or "DEVELOPMENT FEED")
                    {
                        text.FontSize = 12;
                        text.LetterSpacing = 0.4;
                    }
                }
            }

            if (_newsStatusText is not null)
            {
                _newsStatusText.FontSize = 10;
                _newsStatusText.Opacity = 0.78;
                _newsStatusText.Margin = new Thickness(2, -4, 0, 0);
            }
        }

        if (_majorNewsPanel is not null)
            _majorNewsPanel.LayoutUpdated += (_, _) => Polish();
        if (_developmentNewsPanel is not null)
            _developmentNewsPanel.LayoutUpdated += (_, _) => Polish();

        Polish();
    }

    private void PolishNewsTitle(TextBlock textBlock)
    {
        if (!_polishedNewsTitles.Add(textBlock))
            return;

        var original = textBlock.Text?.Trim() ?? string.Empty;
        if (original.Length == 0)
            return;

        var polished = ProfessionalizeNewsTitle(original);
        textBlock.Text = polished;

        if (!string.Equals(original, polished, StringComparison.Ordinal))
            ToolTip.SetTip(textBlock, $"Original GitHub title: {original}");
    }

    private static string ProfessionalizeNewsTitle(string title)
    {
        var text = title.Replace('\r', ' ').Replace('\n', ' ').Trim();

        // Remove common developer-only prefixes while keeping the actual meaning.
        text = Regex.Replace(
            text,
            @"^\s*\[(?:wip|draft|feat(?:ure)?|fix|bugfix|chore|refactor|cleanup|docs?|tests?|ci|build)\]\s*[-:–—]*\s*",
            string.Empty,
            RegexOptions.IgnoreCase);

        text = Regex.Replace(
            text,
            @"^\s*(?:wip|draft|feat(?:ure)?|chore|cleanup|docs?|tests?|ci|build)(?:\([^\)]*\))?\s*[:\-–—]\s*",
            string.Empty,
            RegexOptions.IgnoreCase);

        text = Regex.Replace(
            text,
            @"^\s*refactor(?:\([^\)]*\))?\s*[:\-–—]?\s+",
            "Improve ",
            RegexOptions.IgnoreCase);

        text = Regex.Replace(text, @"^\s*#\d+\s*[-:–—]?\s*", string.Empty);
        text = Regex.Replace(text, @"\s*\(#\d+\)\s*$", string.Empty);
        text = Regex.Replace(text, @"\s+#\d+\s*$", string.Empty);
        text = text.Replace('_', ' ');
        text = Regex.Replace(text, @"\s+", " ").Trim(' ', '-', ':', '–', '—', '.');

        if (text.Length == 0)
            return title.Trim();

        var smallWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "as", "at", "by", "for", "from", "in", "of", "on", "or", "the", "to", "with"
        };

        var acronyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["api"] = "API",
            ["db"] = "DB",
            ["http"] = "HTTP",
            ["https"] = "HTTPS",
            ["json"] = "JSON",
            ["npc"] = "NPC",
            ["npcs"] = "NPCs",
            ["osfr"] = "OSFR",
            ["pr"] = "PR",
            ["prs"] = "PRs",
            ["sql"] = "SQL",
            ["tcp"] = "TCP",
            ["udp"] = "UDP",
            ["ui"] = "UI",
            ["url"] = "URL",
            ["urls"] = "URLs",
            ["xml"] = "XML"
        };

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            var punctuation = word.Length > 0 && ",;:)".Contains(word[^1]) ? word[^1].ToString() : string.Empty;
            var core = punctuation.Length == 0 ? word : word[..^1];

            if (acronyms.TryGetValue(core, out var acronym))
            {
                words[i] = acronym + punctuation;
                continue;
            }

            if (i > 0 && smallWords.Contains(core))
            {
                words[i] = core.ToLowerInvariant() + punctuation;
                continue;
            }

            // Preserve intentional mixed case and existing all-caps names.
            if (core.Any(char.IsUpper) && core.Any(char.IsLower) || (core.Length > 1 && core.All(ch => !char.IsLetter(ch) || char.IsUpper(ch))))
                continue;

            if (core.Length > 0)
                words[i] = char.ToUpperInvariant(core[0]) + core[1..].ToLowerInvariant() + punctuation;
        }

        return string.Join(' ', words);
    }
}
