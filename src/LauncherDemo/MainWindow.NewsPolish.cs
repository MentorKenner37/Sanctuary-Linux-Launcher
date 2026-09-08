using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private bool _newsPolishAttached;

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
}
