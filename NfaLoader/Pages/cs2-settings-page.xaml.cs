using System.Globalization;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NfaLoader.Localization;
using NfaLoader.Services;

namespace NfaLoader.Pages;

public sealed partial class Cs2SettingsPage : Page
{
    private const ulong SteamIdBase = 76561197960265728UL;
    private readonly Cs2SettingsTransferService _transfer = new(backupRoot:
        Path.Combine(AppState.SettingsService.AppFolderPath, "cs2-settings-backups"));
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private List<AccountOption> _accounts = [];
    private Cs2SettingsSnapshot? _snapshot;
    private bool _rebuilding;
    private bool _loading;

    public Cs2SettingsPage()
    {
        InitializeComponent();
        TransferModeSelector.SelectedItem = TransferTab;
        NavigationCacheMode = NavigationCacheMode.Required;
        AppState.BusyChanged += _ => DispatcherQueue.TryEnqueue(UpdateEnabled);
        Loc.LanguageChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            Localize();
            RebuildTargets();
            RebuildSource();
            RefreshBackups();
        });
        Localize();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ReloadAsync();
    }

    private void Localize()
    {
        TitleText.Text = Loc.T("Transfer_Title");
        HintText.Text = Loc.T("Transfer_Hint");
        TransferTab.Text = Loc.T("Transfer_Tab");
        BackupsTab.Text = Loc.T("Transfer_BackupsTab");
        StoppedHint.Message = Loc.T("Transfer_StoppedHint");
        RefreshButton.Content = Loc.T("Transfer_Refresh");
        SourceLabel.Text = Loc.T("Transfer_Source");
        CategoriesLabel.Text = Loc.T("Transfer_Categories");
        ControlsCheck.Content = Loc.T("Transfer_Controls");
        CrosshairCheck.Content = Loc.T("Transfer_Crosshair");
        HudAudioCheck.Content = Loc.T("Transfer_HudAudio");
        VideoCheck.Content = Loc.T("Transfer_Video");
        CustomCheck.Content = Loc.T("Transfer_Custom");
        CopyButton.Content = Loc.T("Transfer_Copy");
        TargetsLabel.Text = Loc.T("Transfer_Targets");
        SearchBox.PlaceholderText = Loc.T("Transfer_Search");
        AutomationProperties.SetName(SourceBox, Loc.T("Transfer_Source"));
        AutomationProperties.SetName(SearchBox, Loc.T("Transfer_Search"));
        AutomationProperties.SetName(TargetsList, Loc.T("Transfer_Targets"));
        AutomationProperties.SetName(BackupsBox, Loc.T("Transfer_Backups"));
        PasteButton.Content = Loc.T("Transfer_Paste");
        SelectVisibleButton.Content = Loc.T("Transfer_SelectVisible");
        ClearSelectionButton.Content = Loc.T("Transfer_ClearSelection");
        BackupsLabel.Text = Loc.T("Transfer_Backups");
        BackupHintText.Text = Loc.T("Transfer_BackupHint");
        RestoreButton.Content = Loc.T("Transfer_Restore");
        UpdateSnapshotText();
        UpdateEnabled();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void TransferMode_Changed(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (TransferPanel is null || BackupsPanel is null) return;
        var backups = sender.SelectedItem == BackupsTab;
        TransferPanel.Visibility = backups ? Visibility.Collapsed : Visibility.Visible;
        BackupsPanel.Visibility = backups ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ReloadAsync()
    {
        if (_loading || AppState.IsBusy) return;
        _loading = true;
        UpdateEnabled();
        try
        {
            _accounts = await Task.Run(ReadAccounts);
            _selected.IntersectWith(_accounts.Select(account => account.SteamId));
            RebuildSource();
            RebuildTargets();
            RefreshBackups();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _loading = false;
            UpdateEnabled();
        }
    }

    private static List<AccountOption> ReadAccounts()
    {
        var paths = SteamPathCoordinator.ResolvePathsOrThrow();
        var local = Directory.Exists(paths.UserdataPath)
            ? Directory.EnumerateDirectories(paths.UserdataPath)
                .Select(Path.GetFileName)
                .Where(name => uint.TryParse(name, out var id) && id > 0)
                .Select(name => new Cs2SettingsSource((SteamIdBase + uint.Parse(name!, CultureInfo.InvariantCulture))
                    .ToString(CultureInfo.InvariantCulture), uint.Parse(name!, CultureInfo.InvariantCulture))).ToList()
            : [];
        var names = SteamAccountNameService.BuildOfflineNames(paths, local);
        var cached = AppState.LoginService.GetCachedLoginAccounts();
        var cachedIds = cached.Select(account => account.SteamId).ToHashSet(StringComparer.Ordinal);
        var history = AppState.HistoryAccounts.ToArray();
        return local.Select(account => account.SteamId64)
            .Concat(names.Keys)
            .Concat(cachedIds)
            .Concat(history.Select(account => account.SteamId))
            .Where(IsPersonalSteamId)
            .Distinct(StringComparer.Ordinal)
            .Select(id =>
            {
                names.TryGetValue(id, out var offline);
                var stored = history.FirstOrDefault(account => account.SteamId == id);
                var cachedAccount = cached.FirstOrDefault(account => account.SteamId == id);
                var name = new[] { stored?.PersonaName, offline?.PersonaName, cachedAccount?.PersonaName,
                    stored?.AccountName, offline?.AccountName, cachedAccount?.AccountName }
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value != id);
                return new AccountOption(id, name, cachedIds.Contains(id));
            })
            .OrderBy(account => account.Name ?? account.SteamId, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool IsPersonalSteamId(string id) => ulong.TryParse(id, out var value)
        && value > SteamIdBase && value <= SteamIdBase + uint.MaxValue;

    private static string AccountLabel(AccountOption account)
    {
        var label = account.Name is null ? account.SteamId : $"{account.Name} ({account.SteamId})";
        return account.Cached ? Loc.Tf("Transfer_Cached_Format", label) : label;
    }

    private void RebuildSource()
    {
        var previous = (SourceBox.SelectedItem as ComboBoxItem)?.Tag as string;
        SourceBox.Items.Clear();
        foreach (var account in _accounts)
        {
            var item = new ComboBoxItem { Content = AccountLabel(account), Tag = account.SteamId };
            SourceBox.Items.Add(item);
            if (account.SteamId == previous) SourceBox.SelectedItem = item;
        }
        if (SourceBox.SelectedIndex < 0 && SourceBox.Items.Count > 0) SourceBox.SelectedIndex = 0;
        UpdateEnabled();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => RebuildTargets();

    private void RebuildTargets()
    {
        _rebuilding = true;
        try
        {
            TargetsList.Items.Clear();
            var search = SearchBox.Text.Trim();
            foreach (var account in _accounts.Where(account =>
                         AccountLabel(account).Contains(search, StringComparison.CurrentCultureIgnoreCase)))
            {
                if (account.SteamId == _snapshot?.SourceSteamId64) continue;
                var description = new StackPanel();
                description.Children.Add(new TextBlock
                {
                    Text = account.Name ?? account.SteamId,
                    Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                description.Children.Add(new TextBlock
                {
                    Text = account.SteamId + (account.Cached ? " · " + Loc.T("Transfer_CachedLabel") : ""),
                    Style = (Style)Application.Current.Resources["FieldLabelTextStyle"]
                });
                var item = new ListViewItem { Content = description, Tag = account.SteamId };
                AutomationProperties.SetName(item, AccountLabel(account));
                TargetsList.Items.Add(item);
                if (_selected.Contains(account.SteamId)) TargetsList.SelectedItems.Add(item);
            }
        }
        finally
        {
            _rebuilding = false;
        }
        UpdateEnabled();
    }

    private void Targets_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding) return;
        foreach (var item in e.RemovedItems.OfType<ListViewItem>()) _selected.Remove((string)item.Tag);
        foreach (var item in e.AddedItems.OfType<ListViewItem>()) _selected.Add((string)item.Tag);
        UpdateEnabled();
    }

    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.IsBusy || _loading) return;
        foreach (var item in TargetsList.Items.OfType<ListViewItem>())
            if (!TargetsList.SelectedItems.Contains(item)) TargetsList.SelectedItems.Add(item);
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.IsBusy || _loading) return;
        _selected.Clear();
        TargetsList.SelectedItems.Clear();
        UpdateEnabled();
    }

    private Cs2SettingsCategory SelectedCategories()
    {
        var categories = Cs2SettingsCategory.None;
        if (ControlsCheck.IsChecked == true) categories |= Cs2SettingsCategory.Controls;
        if (CrosshairCheck.IsChecked == true) categories |= Cs2SettingsCategory.Crosshair;
        if (HudAudioCheck.IsChecked == true) categories |= Cs2SettingsCategory.HudAudio;
        if (VideoCheck.IsChecked == true) categories |= Cs2SettingsCategory.Video;
        if (CustomCheck.IsChecked == true) categories |= Cs2SettingsCategory.CustomConfigs;
        return categories;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.IsBusy || _loading || (SourceBox.SelectedItem as ComboBoxItem)?.Tag is not string source) return;
        var categories = SelectedCategories();
        AppState.SetBusy(true);
        try
        {
            _snapshot = await Task.Run(() => _transfer.Capture(
                SteamPathCoordinator.ResolvePathsOrThrow().UserdataPath, source, categories));
            _selected.Remove(source);
            UpdateSnapshotText();
            ResultText.Text = "";
            RebuildTargets();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            AppState.SetBusy(false);
        }
    }

    private void UpdateSnapshotText()
    {
        if (_snapshot is null)
        {
            SnapshotText.Text = Loc.T("Transfer_EmptySnapshot");
            return;
        }
        var source = _accounts.FirstOrDefault(account => account.SteamId == _snapshot.SourceSteamId64);
        SnapshotText.Text = Loc.Tf("Transfer_Copied_Format", source is null ? _snapshot.SourceSteamId64 : AccountLabel(source),
            _snapshot.FileCount, CategoryLabels(_snapshot.Categories));
        if (_snapshot.MissingCategories != Cs2SettingsCategory.None)
            SnapshotText.Text += "\n" + Loc.Tf("Transfer_Missing_Format", CategoryLabels(_snapshot.MissingCategories));
    }

    private static string CategoryLabels(Cs2SettingsCategory categories) => string.Join(", ",
        new[] { (Cs2SettingsCategory.Controls, "Transfer_Controls"), (Cs2SettingsCategory.Crosshair, "Transfer_Crosshair"),
            (Cs2SettingsCategory.HudAudio, "Transfer_HudAudio"), (Cs2SettingsCategory.Video, "Transfer_Video"),
            (Cs2SettingsCategory.CustomConfigs, "Transfer_Custom") }
        .Where(pair => (categories & pair.Item1) != 0).Select(pair => Loc.T(pair.Item2)));

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.IsBusy || _loading || _snapshot is not { } snapshot) return;
        var targets = TargetsList.SelectedItems.OfType<ListViewItem>().Select(item => (string)item.Tag).ToArray();
        if (targets.Length == 0) return;
        AppState.SetBusy(true);
        try
        {
            var previews = await Task.Run(() => targets.Select(id =>
            {
                try { return (Id: id, Preview: _transfer.Preview(snapshot, id), Error: (string?)null); }
                catch (Exception ex) { return (Id: id, Preview: (Cs2SettingsPreview?)null, Error: ex.Message); }
            }).ToArray());
            var validTargets = previews.Where(item => item.Preview is not null).Select(item => item.Id).ToArray();
            var failures = previews.Where(item => item.Error is not null)
                .Select(item => Loc.Tf("Transfer_Failed_Format", item.Id, item.Error)).ToArray();
            if (validTargets.Length == 0)
            {
                ResultText.Text = string.Join("\n", failures);
                return;
            }
            var details = string.Join("\n\n", previews.Select(item => item.Preview is { } preview
                ? preview.TargetSteamId64 + "\n" + string.Join("\n", preview.Files.Where(file => file.Changed)
                    .Select(file => $"{file.RelativePath} ({CategoryLabels(file.Categories)})"))
                : Loc.Tf("Transfer_Failed_Format", item.Id, item.Error)));
            var dialog = new ContentDialog
            {
                Title = Loc.Tf("Transfer_PreviewTitle_Format", validTargets.Length),
                Content = new ScrollViewer { Content = new TextBlock
                {
                    Text = Loc.T("Transfer_PreviewHint") + "\n\n" + details,
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
                } },
                PrimaryButtonText = Loc.T("Transfer_ConfirmPaste"), CloseButtonText = Loc.T("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var results = await Task.Run(() => _transfer.Apply(snapshot, validTargets));
            ResultText.Text = string.Join("\n", failures.Concat(results.Select(ResultLabel)));
            RefreshBackups();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            AppState.SetBusy(false);
        }
    }

    private static string ResultLabel(Cs2SettingsTransferResult result) => result.Ok
        ? Loc.Tf("Transfer_Success_Format", result.TargetSteamId64, result.FilesWritten)
        : Loc.Tf("Transfer_Failed_Format", result.TargetSteamId64, result.Error);

    private void RefreshBackups()
    {
        var selected = (BackupsBox.SelectedItem as ComboBoxItem)?.Tag as string;
        BackupsBox.Items.Clear();
        foreach (var backup in _transfer.ListBackups())
        {
            var item = new ComboBoxItem
            {
                Content = Loc.Tf("Transfer_BackupItem_Format", backup.CreatedAt.ToLocalTime().ToString("g"),
                    backup.TargetSteamId64, backup.FileCount), Tag = backup.ManifestPath
            };
            BackupsBox.Items.Add(item);
            if (backup.ManifestPath == selected) BackupsBox.SelectedItem = item;
        }
        if (BackupsBox.SelectedIndex < 0 && BackupsBox.Items.Count > 0) BackupsBox.SelectedIndex = 0;
        UpdateEnabled();
    }

    private void Backup_Changed(object sender, SelectionChangedEventArgs e) => UpdateEnabled();

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.IsBusy || _loading || (BackupsBox.SelectedItem as ComboBoxItem)?.Tag is not string manifest) return;
        AppState.SetBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                Title = Loc.T("Transfer_Restore"),
                Content = (BackupsBox.SelectedItem as ComboBoxItem)?.Content + "\n\n" + Loc.T("Transfer_RestoreHint"),
                PrimaryButtonText = Loc.T("Transfer_Restore"), CloseButtonText = Loc.T("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await Task.Run(() => _transfer.Restore(manifest,
                SteamPathCoordinator.ResolvePathsOrThrow().UserdataPath));
            ResultText.Text = ResultLabel(result);
            RestoreResultText.Text = ResultText.Text;
            AppState.ShowStatus(ResultText.Text, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            RefreshBackups();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            AppState.SetBusy(false);
        }
    }

    private void ShowError(Exception ex)
    {
        ResultText.Text = ex.Message;
        RestoreResultText.Text = ex.Message;
        AppState.ShowStatus(ex.Message, InfoBarSeverity.Error);
    }

    private void UpdateEnabled()
    {
        if (PasteButton is null) return;
        var ready = !AppState.IsBusy && !_loading;
        SourceBox.IsEnabled = ready;
        CopyButton.IsEnabled = ready && SourceBox.Items.Count > 0;
        RefreshButton.IsEnabled = ready;
        ControlsCheck.IsEnabled = ready;
        CrosshairCheck.IsEnabled = ready;
        HudAudioCheck.IsEnabled = ready;
        VideoCheck.IsEnabled = ready;
        CustomCheck.IsEnabled = ready;
        SearchBox.IsEnabled = ready;
        TargetsList.IsEnabled = ready;
        SelectVisibleButton.IsEnabled = ready && TargetsList.Items.Count > 0;
        ClearSelectionButton.IsEnabled = ready && _selected.Count > 0;
        BackupsBox.IsEnabled = ready;
        RestoreButton.IsEnabled = ready && BackupsBox.SelectedItem is not null;
        var visibleSelected = TargetsList.SelectedItems.Count;
        PasteButton.IsEnabled = ready && _snapshot is { FileCount: > 0 } && visibleSelected > 0;
        SelectionText.Text = _accounts.Count == 0 ? Loc.T("Transfer_NoAccounts")
            : Loc.Tf("Transfer_Selected_Format", visibleSelected, _selected.Count);
    }

    private sealed record AccountOption(string SteamId, string? Name, bool Cached);
}
