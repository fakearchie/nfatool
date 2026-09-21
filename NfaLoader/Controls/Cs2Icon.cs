using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using NfaLoader.Services;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace NfaLoader.Controls;

/// <summary>
/// A small CS2 icon (Steam, Key, Timer, Vac, Refresh, Clock) drawn in the secondary text color, and kept in that
/// color when the theme changes.
/// </summary>
public sealed partial class Cs2Icon : UserControl
{
    private readonly Path _path = new() { Stretch = Stretch.Uniform };
    private string _kind = "";

    public Cs2Icon()
    {
        Content = _path;
        IsTabStop = false;
        Size = 16;
        ApplyFill();
        ActualThemeChanged += (_, _) => ApplyFill();
    }

    public string Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            _path.Data = Cs2Art.DataFor(value) is { } data
                ? (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data)
                : null;
        }
    }

    public double Size
    {
        get => _path.Width;
        set
        {
            _path.Width = value;
            _path.Height = value;
        }
    }

    private void ApplyFill() => _path.Fill = FormatHelper.GetStatusBrush(InfoBarSeverity.Informational);
}
