using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.UI;

namespace NfaLoader.Controls;

/// <summary>A CS2 Premier rating shown the way the game shows it. A null rating draws the game's dashed badge.</summary>
public sealed partial class PremierRatingBadge : UserControl
{
    // panorama/styles/rating_emblem.css: color-csrating-tier-0 to tier-6, one tier per 5,000 rating.
    private static readonly Color[] TierColors =
    [
        Color.FromArgb(255, 0xB0, 0xC3, 0xD9),
        Color.FromArgb(255, 0x8C, 0xC6, 0xFF),
        Color.FromArgb(255, 0x6A, 0x7D, 0xFF),
        Color.FromArgb(255, 0xC1, 0x66, 0xFF),
        Color.FromArgb(255, 0xF0, 0x3C, 0xFF),
        Color.FromArgb(255, 0xEB, 0x4B, 0x4B),
        Color.FromArgb(255, 0xFF, 0xD7, 0x00),
    ];

    // The grey the XAML was drawn with, kept per brush so every retint starts from the original shades.
    private readonly List<(SolidColorBrush Brush, Color Grey)> _solid = [];
    private readonly List<(GradientStop Stop, Color Grey)> _stops = [];
    private int? _rating;
    private int _tier = -1;

    public PremierRatingBadge()
    {
        InitializeComponent();

        foreach (var path in BadgeCanvas.Children.OfType<Path>())
        {
            if (path == NoneDashes)
            {
                continue;
            }

            switch (path.Fill)
            {
                case SolidColorBrush solid:
                    _solid.Add((solid, solid.Color));
                    break;
                case LinearGradientBrush gradient:
                    _stops.AddRange(gradient.GradientStops.Select(stop => (stop, stop.Color)));
                    break;
            }
        }

        Apply();
    }

    /// <summary>The Premier rating, or null when the account has none.</summary>
    public int? Rating
    {
        get => _rating;
        set
        {
            _rating = value is > 0 ? value : null;
            Apply();
        }
    }

    private void Apply()
    {
        var tier = _rating is { } rating ? Math.Clamp(rating / 5000, 0, TierColors.Length - 1) : 0;
        if (tier != _tier)
        {
            _tier = tier;
            var wash = TierColors[tier];
            foreach (var (brush, grey) in _solid)
            {
                brush.Color = Wash(grey, wash);
            }

            foreach (var (stop, grey) in _stops)
            {
                stop.Color = Wash(grey, wash);
            }

            // The game washes the label with the tier color and brightens it.
            var label = new SolidColorBrush(Lighten(wash, 0.45));
            MajorText.Foreground = label;
            MinorText.Foreground = label;
            NoneDashes.Fill = new SolidColorBrush(Lighten(wash, 0.45));
        }

        var hasRating = _rating is not null;
        NoneDashes.Visibility = hasRating ? Visibility.Collapsed : Visibility.Visible;
        ValueGrid.Visibility = hasRating ? Visibility.Visible : Visibility.Collapsed;
        if (_rating is not { } value)
        {
            return;
        }

        // 12,345 is drawn as a large "12," and a smaller "345". Below 1,000 there is only the small part.
        var major = value >= 1000 ? $"{value / 1000}," : "";
        var minor = value >= 1000 ? (value % 1000).ToString("000") : value.ToString();
        MajorText.Text = MajorShadow.Text = major;
        MinorText.Text = MinorShadow.Text = minor;
    }

    // Panorama's wash-color keeps the shading and replaces the hue: the grey level scales the tier color.
    private static Color Wash(Color grey, Color wash)
    {
        var level = grey.R / 255.0 * 1.2;
        return Color.FromArgb(
            grey.A,
            (byte)Math.Min(255, wash.R * level),
            (byte)Math.Min(255, wash.G * level),
            (byte)Math.Min(255, wash.B * level));
    }

    private static Color Lighten(Color color, double amount) => Color.FromArgb(
        255,
        (byte)(color.R + (255 - color.R) * amount),
        (byte)(color.G + (255 - color.G) * amount),
        (byte)(color.B + (255 - color.B) * amount));
}
