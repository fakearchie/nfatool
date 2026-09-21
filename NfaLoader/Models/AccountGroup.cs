namespace NfaLoader.Models;

/// <summary>
/// Definition of a user-defined account group (name + order). Membership is stored on each account's <c>GroupIds</c>, linked by the stable <see cref="Id"/>,
/// so renaming a group does not rewrite any account. The definitions are stored in AppSettings.Groups in settings.json.
/// Plain data class: the UI wraps every group in a ComboBoxItem/ToggleMenuFlyoutItem (with string content), so instances of this type never cross the WinRT ABI.
/// </summary>
public sealed class AccountGroup
{
    /// <summary>Stable identifier (referenced by account GroupIds, so renaming does not affect membership).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>Group display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Display order; lower values come first.</summary>
    public int Order { get; set; }
}
