using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using NfaLoader.Localization;
using NfaLoader.Models;
using NfaLoader.Services;
using Windows.Foundation;

namespace NfaLoader.Pages;

public sealed partial class LoadoutPage : Page, INotifyPropertyChanged
{
    private const double DragThresholdPixels = 8;

    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    private bool _currentCt;            // Team being edited: false=T, true=CT
    private bool _built;
    private CsLoadoutPreset _working = new();

    // The page does its own pointer dragging (not system OLE drag and drop: in an elevated process WinUI3's CanDrag/AllowDrop stops working entirely,
    // which shows up as "can't drag, but left click works"; pointer events are not affected by elevation, and every user goes down the same path).
    // State lives in one object that is set to null when the operation ends, so no stale fields carry over between drags.
    private sealed class DragOperation
    {
        public UIElement Origin = null!;
        public uint PointerId;
        public uint Def;
        public CsLoadoutGroup Group;
        public uint? FromSlot;   // Set = dragged from an equipped slot (move/swap); null = dragged from the pool (equip).
        public Point PressPosition;
        public bool Active;      // Only counts as started once past the drag threshold
        public Border? Ghost;
        public TextBlock? GhostCaption;
    }

    private DragOperation? _drag;
    private CellView? _dropTarget;
    private bool _suppressNextTap;

    private readonly Dictionary<uint, CellView> _cells = new();
    private readonly ObservableCollection<LoadoutWeaponTile> _poolItems = new();

    public LoadoutPage()
    {
        InitializeComponent();
        PoolGrid.ItemsSource = _poolItems;
        TeamSelector.SelectedItem = TeamTItem;
        Loc.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML binding entry point: {x:Bind Strings.Get('Key'), Mode=OneWay}.</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _working = AppState.SettingsService.Load().Loadout.Clone();
        BuildCells();
        RefreshAll();
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            // Static x:Bind text is recomputed from Strings; group headers are built in code, so rebuild them;
            // pool tiles bind DisplayName OneTime, so clear them and let the diff sync rebuild them all in the new language.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));
            if (_built)
            {
                BuildCells();
                _poolItems.Clear();
                RefreshAll();
            }
        });
    }

    // References to the visual elements of one fixed slot.
    private sealed class CellView
    {
        public Border Root = null!;
        public Border Bg = null!;
        public Image Image = null!;
        public TextBlock Empty = null!;
        public TextBlock Name = null!;
        public Border Accent = null!;
        public bool Equipped;
        public uint Slot;
        public CsLoadoutGroup Group;
    }

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) =>
        new(Windows.UI.Color.FromArgb(a, r, g, b));

    // Slots stay dark in both themes: the weapon icons are white, so a light tile would hide them.
    private static readonly SolidColorBrush TileBrush = Brush(0xFF, 0x2A, 0x2C, 0x31);
    private static readonly SolidColorBrush TileHoverBrush = Brush(0xFF, 0x33, 0x36, 0x3C);
    private static readonly SolidColorBrush EmptyTileBrush = Brush(0xFF, 0x22, 0x24, 0x28);
    private static readonly SolidColorBrush TileBorderBrush = Brush(0xFF, 0x38, 0x3B, 0x42);
    private static readonly SolidColorBrush NameBrush = Brush(0xFF, 0xB9, 0xBE, 0xC7);
    private static readonly SolidColorBrush EmptyTextBrush = Brush(0xFF, 0x6B, 0x70, 0x78);

    // The in-game side colors, used for the accent on equipped slots and the drop highlight.
    private SolidColorBrush TeamAccent => _currentCt
        ? Brush(0xFF, 0x5B, 0x8D, 0xEF)
        : Brush(0xFF, 0xD6, 0xA2, 0x3E);

    private void BuildCells()
    {
        _cells.Clear();
        BuildColumn(Col0Host,
        [
            (Loc.T("Loadout_Group_Starter"), CsLoadoutGroup.StarterPistol, [CsWeaponCatalog.StarterPistolSlot]),
            (Loc.T("Loadout_Group_Other"), CsLoadoutGroup.OtherPistol, CsWeaponCatalog.OtherPistolSlots)
        ]);
        BuildColumn(Col1Host, [(Loc.T("Loadout_Group_Mid"), CsLoadoutGroup.Mid, CsWeaponCatalog.MidSlots)]);
        BuildColumn(Col2Host, [(Loc.T("Loadout_Group_Rifle"), CsLoadoutGroup.Rifle, CsWeaponCatalog.RifleSlots)]);
        _built = true;
    }

    // Each section: an Auto row header + several Star rows of slots. Star rows stretch the slots to fill the column height → column bottoms line up.
    private void BuildColumn(Grid host, (string Title, CsLoadoutGroup Group, IReadOnlyList<uint> Slots)[] sections)
    {
        host.Children.Clear();
        host.RowDefinitions.Clear();

        var row = 0;
        foreach (var (title, group, slots) in sections)
        {
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new TextBlock
            {
                Text = title,
                Style = (Style)Resources["LoadoutSectionHeaderStyle"],
                Margin = new Thickness(0, row == 0 ? 0 : 10, 0, 8)
            };
            Grid.SetRow(header, row);
            host.Children.Add(header);
            row++;

            foreach (var slot in slots)
            {
                host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                var cell = BuildCell(group, slot);
                Grid.SetRow(cell.Root, row);
                host.Children.Add(cell.Root);
                _cells[slot] = cell;
                row++;
            }
        }
    }

    private CellView BuildCell(CsLoadoutGroup group, uint slot)
    {
        var bg = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = EmptyTileBrush,
            BorderThickness = new Thickness(1),
            BorderBrush = TileBorderBrush
        };
        // A thin bar in the side's color marks an equipped slot.
        var accent = new Border
        {
            Width = 3,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(6, 0, 0, 6),
            Visibility = Visibility.Collapsed
        };
        // Name on the left, icon filling the right: the slots are wide and short, so side by side uses the space.
        // A shared height cap keeps a pistol and a sniper rifle at the same scale.
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            MaxHeight = 48,
            Margin = new Thickness(0, 10, 18, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        var empty = new TextBlock
        {
            Text = Loc.T("Loadout_Slot_Empty"),
            FontSize = 12,
            Foreground = EmptyTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var name = new TextBlock
        {
            FontSize = 13,
            Foreground = NameBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed
        };

        var content = new Grid();
        content.Children.Add(bg);
        content.Children.Add(accent);
        content.Children.Add(image);
        content.Children.Add(empty);
        content.Children.Add(name);

        var root = new Border
        {
            Child = content,
            Margin = new Thickness(0, 0, 0, 8)
        };

        var cell = new CellView { Root = root, Bg = bg, Image = image, Empty = empty, Name = name, Accent = accent, Slot = slot, Group = group };
        root.Tag = cell;
        root.PointerPressed += Cell_PointerPressed;
        root.PointerMoved += DragSource_PointerMoved;
        root.PointerReleased += DragSource_PointerReleased;
        root.PointerCaptureLost += DragSource_PointerCaptureLost;
        root.PointerCanceled += DragSource_PointerCanceled;
        root.Tapped += Cell_Tapped;
        root.PointerEntered += (_, _) => { if (cell.Equipped) cell.Bg.Background = TileHoverBrush; };
        root.PointerExited += (_, _) => cell.Bg.Background = cell.Equipped ? TileBrush : EmptyTileBrush;
        return cell;
    }

    private void RefreshAll()
    {
        CancelDrag();
        var slots = _working.SlotsFor(_currentCt);
        var accent = TeamAccent;
        foreach (var cell in _cells.Values)
        {
            UpdateCell(cell, slots, accent);
        }

        SyncPool(slots);
    }

    private static void UpdateCell(CellView cell, Dictionary<uint, uint> slots, SolidColorBrush accent)
    {
        if (slots.TryGetValue(cell.Slot, out var def) && CsWeaponCatalog.ByDef(def) is { } weapon)
        {
            cell.Equipped = true;
            cell.Bg.Background = TileBrush;
            cell.Image.Source = new SvgImageSource(new Uri(weapon.IconUri));
            cell.Image.Visibility = Visibility.Visible;
            cell.Empty.Visibility = Visibility.Collapsed;
            cell.Name.Text = weapon.LocalizedName;
            cell.Name.Visibility = Visibility.Visible;
            cell.Accent.Background = accent;
            cell.Accent.Visibility = Visibility.Visible;
        }
        else
        {
            cell.Equipped = false;
            cell.Bg.Background = EmptyTileBrush;
            cell.Image.Source = null;
            cell.Image.Visibility = Visibility.Collapsed;
            cell.Empty.Visibility = Visibility.Visible;
            cell.Name.Visibility = Visibility.Collapsed;
            cell.Accent.Visibility = Visibility.Collapsed;
        }
    }

    // Diff-syncs the pool: equip/unequip only adds or removes the matching tile, so a Clear + full rebuild doesn't jump the scroll back to the top and reload every icon.
    private void SyncPool(Dictionary<uint, uint> slots)
    {
        var equipped = slots.Values.ToHashSet();
        var desired = new List<CsWeapon>();
        foreach (var group in CsWeaponCatalog.EditorGroups)
        {
            foreach (var weapon in CsWeaponCatalog.ForTeamGroup(_currentCt, group))
            {
                if (!equipped.Contains(weapon.Def))
                {
                    desired.Add(weapon);
                }
            }
        }

        var desiredDefs = desired.Select(w => w.Def).ToHashSet();
        for (var i = _poolItems.Count - 1; i >= 0; i--)
        {
            if (!desiredDefs.Contains(_poolItems[i].Weapon.Def))
            {
                _poolItems.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < _poolItems.Count && _poolItems[i].Weapon.Def == desired[i].Def)
            {
                continue;
            }

            var existing = -1;
            for (var j = i + 1; j < _poolItems.Count; j++)
            {
                if (_poolItems[j].Weapon.Def == desired[i].Def)
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
            {
                _poolItems.Move(existing, i);
            }
            else
            {
                _poolItems.Insert(i, new LoadoutWeaponTile(desired[i]));
            }
        }
    }

    private void TeamSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _currentCt = TeamSelector.SelectedItem == TeamCtItem;
        if (_built)
        {
            RefreshAll();
        }
    }

    // ---- Manual pointer dragging ----

    private void Cell_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CellView cell ||
            !_working.SlotsFor(_currentCt).TryGetValue(cell.Slot, out var def) ||
            !IsPrimaryPress(e, cell.Root))
        {
            return;
        }

        BeginDrag(cell.Root, e, def, cell.Group, cell.Slot);
    }

    private void PoolItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LoadoutWeaponTile tile } origin ||
            !IsPrimaryPress(e, origin))
        {
            return;
        }

        BeginDrag(origin, e, tile.Weapon.Def, CsWeaponCatalog.GroupOf(tile.Weapon), fromSlot: null);
    }

    private static bool IsPrimaryPress(PointerRoutedEventArgs e, UIElement origin) =>
        e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse ||
        e.GetCurrentPoint(origin).Properties.IsLeftButtonPressed;

    private void BeginDrag(UIElement origin, PointerRoutedEventArgs e, uint def, CsLoadoutGroup group, uint? fromSlot)
    {
        CancelDrag();
        _suppressNextTap = false;

        if (!origin.CapturePointer(e.Pointer))
        {
            return;
        }

        _drag = new DragOperation
        {
            Origin = origin,
            PointerId = e.Pointer.PointerId,
            Def = def,
            Group = group,
            FromSlot = fromSlot,
            PressPosition = e.GetCurrentPoint(DragLayer).Position
        };
    }

    private void DragSource_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag ||
            !ReferenceEquals(sender, drag.Origin) ||
            e.Pointer.PointerId != drag.PointerId)
        {
            return;
        }

        // Everything uses DragLayer coordinates: the ghost is a child of DragLayer (Canvas), and hit testing maps the slots into the same space,
        // so PageRoot's Padding can't offset the ghost or misalign it with the highlighted drop slot.
        var position = e.GetCurrentPoint(DragLayer).Position;
        if (!drag.Active)
        {
            var dx = position.X - drag.PressPosition.X;
            var dy = position.Y - drag.PressPosition.Y;
            if (dx * dx + dy * dy < DragThresholdPixels * DragThresholdPixels)
            {
                return;
            }

            ActivateDrag(drag);
        }

        Canvas.SetLeft(drag.Ghost!, position.X + 14);
        Canvas.SetTop(drag.Ghost!, position.Y + 10);
        UpdateDropTarget(position, drag);
    }

    private void DragSource_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is not { } drag ||
            !ReferenceEquals(sender, drag.Origin) ||
            e.Pointer.PointerId != drag.PointerId)
        {
            return;
        }

        var target = _dropTarget;
        var shouldDrop = drag.Active && target is not null;
        CancelDrag();

        if (shouldDrop)
        {
            PerformDrop(drag, target!);
        }
    }

    // Only cancel when the lost or canceled pointer is the one doing the drag, so with several pointers (touch + pen) lifting one
    // doesn't kill a drag another pointer is doing.
    private void DragSource_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is { } drag && e.Pointer.PointerId == drag.PointerId)
        {
            CancelDrag();
        }
    }

    private void DragSource_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_drag is { } drag && e.Pointer.PointerId == drag.PointerId)
        {
            CancelDrag();
        }
    }

    private void ActivateDrag(DragOperation drag)
    {
        drag.Active = true;
        // Once the drag has really started, the origin slot's following Tapped must not trigger "click to unequip".
        _suppressNextTap = true;

        var image = new Image { Width = 64, Height = 26, Stretch = Stretch.Uniform };
        if (CsWeaponCatalog.ByDef(drag.Def) is { } weapon)
        {
            image.Source = new SvgImageSource(new Uri(weapon.IconUri));
        }

        var caption = new TextBlock
        {
            FontSize = 12,
            Foreground = Brush(0xFF, 0xE6, 0xE8, 0xEC),
            VerticalAlignment = VerticalAlignment.Center
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(image);
        panel.Children.Add(caption);

        drag.GhostCaption = caption;
        drag.Ghost = new Border
        {
            Background = Brush(0xF2, 0x2A, 0x2C, 0x31),
            BorderBrush = TileBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 12, 6),
            IsHitTestVisible = false
        };
        drag.Ghost.Child = panel;
        DragLayer.Children.Add(drag.Ghost);
        UpdateGhostCaption(drag, validTarget: false);
    }

    private void UpdateDropTarget(Point position, DragOperation drag)
    {
        CellView? target = null;
        foreach (var cell in _cells.Values)
        {
            if (cell.Group != drag.Group)
            {
                continue;
            }

            var topLeft = cell.Root.TransformToVisual(DragLayer).TransformPoint(new Point(0, 0));
            if (position.X >= topLeft.X && position.X <= topLeft.X + cell.Root.ActualWidth &&
                position.Y >= topLeft.Y && position.Y <= topLeft.Y + cell.Root.ActualHeight)
            {
                target = cell;
                break;
            }
        }

        SetDropTarget(target);
        UpdateGhostCaption(drag, target is not null);
    }

    private void SetDropTarget(CellView? target)
    {
        if (ReferenceEquals(_dropTarget, target))
        {
            return;
        }

        if (_dropTarget is { } previous)
        {
            previous.Bg.BorderBrush = TileBorderBrush;
            previous.Bg.BorderThickness = new Thickness(1);
        }

        _dropTarget = target;
        if (target is not null)
        {
            target.Bg.BorderBrush = TeamAccent;
            target.Bg.BorderThickness = new Thickness(2);
        }
    }

    private void UpdateGhostCaption(DragOperation drag, bool validTarget)
    {
        if (drag.GhostCaption is null || drag.Ghost is null)
        {
            return;
        }

        var name = CsWeaponCatalog.ByDef(drag.Def)?.LocalizedName ?? drag.Def.ToString();
        drag.GhostCaption.Text = validTarget
            ? $"{Loc.T(drag.FromSlot is null ? "Loadout_Drag_Equip" : "Loadout_Drag_Move")} · {name}"
            : name;
        drag.Ghost.Opacity = validTarget ? 1.0 : 0.7;
    }

    private void CancelDrag()
    {
        if (_drag is { } drag)
        {
            if (drag.Ghost is { } ghost)
            {
                DragLayer.Children.Remove(ghost);
            }

            // The OS-level pointer capture must be released explicitly: if the drag ends by a path other than PointerReleased (RefreshAll from switching teams,
            // RefreshAll after a successful drop, etc.), the capture leaks to origin, later pointer events get misrouted and new drags can't start.
            drag.Origin.ReleasePointerCaptures();
        }

        SetDropTarget(null);
        _drag = null;
    }

    private void PerformDrop(DragOperation drag, CellView cell)
    {
        if (cell.Group != drag.Group)
        {
            return;
        }

        var slots = _working.SlotsFor(_currentCt);

        if (drag.FromSlot is { } fromSlot)
        {
            // Moving an equipped weapon: move if the target is empty, swap if it holds a gun.
            if (fromSlot == cell.Slot)
            {
                return;
            }

            if (slots.TryGetValue(cell.Slot, out var targetDef))
            {
                slots[cell.Slot] = drag.Def;
                slots[fromSlot] = targetDef;
            }
            else
            {
                slots[cell.Slot] = drag.Def;
                slots.Remove(fromSlot);
            }
        }
        else
        {
            // Equip from the pool (any gun already there is replaced and goes back to the pool on refresh).
            slots[cell.Slot] = drag.Def;
        }

        Persist();
        RefreshAll();
    }

    private void Cell_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (_suppressNextTap)
        {
            _suppressNextTap = false;
            return;
        }

        if ((sender as FrameworkElement)?.Tag is CellView cell &&
            _working.SlotsFor(_currentCt).Remove(cell.Slot))
        {
            Persist();
            RefreshAll();
        }
    }

    private void Persist()
    {
        var settings = AppState.SettingsService.Load();
        settings.Loadout = _working.Clone();
        AppState.SettingsService.Save(settings);
    }
}
