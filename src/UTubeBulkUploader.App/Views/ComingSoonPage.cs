using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace UTubeBulkUploader.Views;

/// <summary>Placeholder for screens that arrive in later updates.</summary>
public sealed partial class ComingSoonPage : Page
{
    public ComingSoonPage(string title, string glyph, string text)
    {
        var tertiary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        var secondary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var grid = new Grid { Margin = (Thickness)Application.Current.Resources["PageMargin"], RowSpacing = 12 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] });

        var empty = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 480 };
        empty.Children.Add(new FontIcon { Glyph = glyph, FontSize = 40, Foreground = tertiary });
        empty.Children.Add(new TextBlock { Text = "Coming in a later update", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        empty.Children.Add(new TextBlock { Text = text, Foreground = secondary, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        Grid.SetRow(empty, 1);
        grid.Children.Add(empty);
        Content = grid;
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
