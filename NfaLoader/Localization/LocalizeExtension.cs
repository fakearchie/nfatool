using Microsoft.UI.Xaml.Markup;

namespace NfaLoader.Localization;

/// <summary>
/// Markup extension that localizes static text inside a DataTemplate: {loc:Localize Key=History_Card_QuickLogin}.
/// Inside a DataTemplate the x:Bind root is the data item, not the page, so it cannot reach the page's Strings; that text uses this extension instead.
/// The value is read when the element is realized (loaded); after a language switch, already realized containers only refresh once the list is rebuilt. That is fine for tooltips and similar text.
/// </summary>
public sealed partial class LocalizeExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => Loc.T(Key);
}
