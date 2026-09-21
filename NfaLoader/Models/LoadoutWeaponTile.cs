using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace NfaLoader.Models;

// View model for one weapon tile on the loadout page: icon + name + selected state + position within its category.
// Accessed on the UI thread only. Visibility is exposed through computed properties, so no value converters are needed under AOT.
internal sealed partial class LoadoutWeaponTile : INotifyPropertyChanged
{
    public LoadoutWeaponTile(CsWeapon weapon)
    {
        Weapon = weapon;
        IconUri = new Uri(weapon.IconUri);
    }

    public CsWeapon Weapon { get; }

    public Uri IconUri { get; }

    public string DisplayName => Weapon.LocalizedName;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            Raise(nameof(IsSelected));
            Raise(nameof(SelectedVisibility));
        }
    }

    private string _positionText = "";
    public string PositionText
    {
        get => _positionText;
        set
        {
            if (_positionText == value)
            {
                return;
            }

            _positionText = value;
            Raise(nameof(PositionText));
            Raise(nameof(PositionVisibility));
        }
    }

    public Visibility SelectedVisibility => _isSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PositionVisibility =>
        string.IsNullOrEmpty(_positionText) ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
