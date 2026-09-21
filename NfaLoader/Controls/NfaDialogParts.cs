using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NfaLoader.Services;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace NfaLoader.Controls;

/// <summary>Pieces shared by the nfa.pub dialogs, so the purchase and replacement windows look like one family.</summary>
internal static class NfaDialogParts
{
    public const double Width = 400;

    public static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    public static Path SteamLogo(double size) => new()
    {
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center,
        Fill = Resource("TextFillColorSecondaryBrush"),
        Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), Cs2Art.SteamData),
    };

    /// <summary>The product as CS2 shows it: the Prime emblem, a Premier rating badge, or the Premier logo.</summary>
    public static FrameworkElement? ProductArt(NfaStockItem item)
    {
        FrameworkElement? art = item.Type switch
        {
            "prime" => new Image { Source = new BitmapImage(new Uri(Cs2Art.PrimeImage)), Width = 52, Height = 52 },
            "premier-10k" => new PremierRatingBadge { Rating = 10000, Height = 30 },
            "premier-15k" => new PremierRatingBadge { Rating = 15000, Height = 30 },
            "premier-20k" => new PremierRatingBadge { Rating = 20000, Height = 30 },
            _ when item.Type.StartsWith("premier", StringComparison.Ordinal) =>
                new Image { Source = new SvgImageSource(new Uri(Cs2Art.PremierLogo)), Width = 92, Height = 16 },
            _ => null,
        };

        if (art is null)
        {
            return null;
        }

        art.HorizontalAlignment = HorizontalAlignment.Center;
        art.VerticalAlignment = VerticalAlignment.Center;

        // A fixed dark tile: the game art is drawn for CS2's dark menus and would wash out on a light one.
        return new Border
        {
            Width = 112,
            Height = 68,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1C, 0x1E, 0x22)),
            Child = art,
        };
    }

    /// <summary>A label on the left and a value on the right.</summary>
    public static Grid Row(string label, string value, Brush? valueBrush = null, bool strong = false)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Resource("TextFillColorSecondaryBrush"),
            FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        });

        var valueText = new TextBlock
        {
            Text = value,
            FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        };
        if (valueBrush is not null)
        {
            valueText.Foreground = valueBrush;
        }

        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        return grid;
    }

    /// <summary>
    /// A dialog with a single button puts it in the right half of the button row. This spans it across the whole row
    /// once the dialog opens, since it is the only way out.
    /// </summary>
    public static void StretchLoneButton(ContentDialog dialog)
    {
        dialog.Opened += (_, _) =>
        {
            if (FindByName(dialog, "CloseButton") is Button button && VisualTreeHelper.GetParent(button) is Grid row)
            {
                Grid.SetColumn(button, 0);
                Grid.SetColumnSpan(button, row.ColumnDefinitions.Count);
            }
        };
    }

    private static DependencyObject? FindByName(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Name: var childName } && childName == name)
            {
                return child;
            }

            if (FindByName(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public static Border Divider() => new()
    {
        Height = 1,
        Background = Resource("DividerStrokeColorDefaultBrush"),
    };
}
