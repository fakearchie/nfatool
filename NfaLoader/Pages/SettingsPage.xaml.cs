using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NfaLoader.Localization;
using NfaLoader.Services;

namespace NfaLoader.Pages;

public sealed partial class SettingsPage : Page, INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

    // Setting ComboBox.SelectedItem in code fires SelectionChanged, so this flag tells "user selection" apart from "initial sync" to avoid write-back/applying twice.
    private bool _syncing;

    // Bumped per key check, so a slow answer for an old key never overwrites the state of a newer one.
    private int _nfaCheckGeneration;

    public SettingsPage()
    {
        InitializeComponent();

        // After a language switch, re-evaluate every {x:Bind Strings.Get(...), Mode=OneWay} on this page (theme item text and so on).
        Loc.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML binding entry point: {x:Bind Strings.Get('Key'), Mode=OneWay}.</summary>
    internal LocalizedStrings Strings => Loc.Strings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SyncFromSettings();
        RefreshNfaKeyState(verify: true);
    }

    private void OnLanguageChanged()
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Strings)));

            // Theme item Content has already switched language via x:Bind, but the closed ComboBox "selection box" shows a snapshot of the selected item content
            // and does not re-read it, so reset SelectedItem once to force a refresh. It all runs synchronously in one UI callback, the empty state in between never renders, no flicker.
            var theme = ThemeComboBox.SelectedItem;
            if (theme is not null)
            {
                _syncing = true;
                ThemeComboBox.SelectedItem = null;
                ThemeComboBox.SelectedItem = theme;
                _syncing = false;
            }

            // When unset, SteamPathText shows localized placeholder text that must follow the language (when set it is a neutral path, refreshing has no side effects).
            UpdateSteamPathText();

            // Source account options/selected text are built with Settings_Cs2Sync_SourceItem_Format, so rebuild them for the new language.
            RefreshCs2SyncSources();
        });
    }

    /// <summary>Select the dropdown items for the current language and saved theme; SelectionChanged handling is blocked meanwhile.</summary>
    private void SyncFromSettings()
    {
        _syncing = true;
        try
        {
            var settings = AppState.SettingsService.Load();
            ThemeComboBox.SelectedItem = FindByTag(ThemeComboBox, settings.Theme) ?? ThemeComboBox.Items[0];
        }
        finally
        {
            _syncing = false;
        }

        DataFolderPathText.Text = AppState.SettingsService.AppFolderPath;
        UpdateSteamPathText();
        RefreshCs2SyncSources();
    }

    /// <summary>Show the persisted Steam install folder; when unset, show placeholder text (startup detects it automatically).</summary>
    private void UpdateSteamPathText()
    {
        SteamPathText.Text = SteamPathCoordinator.GetPersistedInstallPath() ?? Loc.T("Settings_SteamPath_NotSet");
    }

    private static ComboBoxItem? FindByTag(ComboBox combo, string tag) =>
        combo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string value &&
                string.Equals(value, tag, StringComparison.OrdinalIgnoreCase));

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || (ThemeComboBox.SelectedItem as ComboBoxItem)?.Tag is not string theme)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.Theme = theme;
        AppState.SettingsService.Save(settings);

        MainWindow.Instance?.ApplyTheme(theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        });
    }

    // ---------- nfa.pub API key ----------

    private enum NfaKeyState { Missing, Checking, Valid, Warning, Invalid }

    private void RefreshNfaKeyState(bool verify)
    {
        var key = AppState.GetNfaApiKey();
        var hasKey = !string.IsNullOrWhiteSpace(key);
        NfaRemoveKeyButton.IsEnabled = hasKey;
        NfaApiKeyBox.PlaceholderText = Loc.T(hasKey ? "Nfa_Settings_KeySavedPlaceholder" : "Nfa_Settings_KeyPlaceholder");

        if (!hasKey)
        {
            _nfaCheckGeneration++;
            SetNfaKeyState(NfaKeyState.Missing, Loc.T("Nfa_Settings_NotSet"));
            return;
        }

        if (verify)
        {
            _ = VerifyNfaKeyAsync(key!, saveIfUsable: false);
        }
    }

    private async void NfaSaveKeyButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveTypedNfaKeyAsync();
    }

    private async void NfaApiKeyBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            await SaveTypedNfaKeyAsync();
        }
    }

    private async Task SaveTypedNfaKeyAsync()
    {
        var typed = NfaApiKeyBox.Password.Trim();
        if (typed.Length == 0)
        {
            SetNfaKeyState(NfaKeyState.Invalid, Loc.T("Nfa_Settings_Error_Empty"));
            return;
        }

        await VerifyNfaKeyAsync(typed, saveIfUsable: true);
    }

    /// <summary>
    /// Checks a key against the balance endpoint. A key the API rejects is never saved. A key that could not be
    /// checked, because nfa.pub was unreachable, is saved with a warning: refusing it would lock the user out offline.
    /// </summary>
    private async Task VerifyNfaKeyAsync(string key, bool saveIfUsable)
    {
        var generation = ++_nfaCheckGeneration;
        NfaSaveKeyButton.IsEnabled = false;
        SetNfaKeyState(NfaKeyState.Checking, Loc.T("Nfa_Settings_Checking"));

        try
        {
            var balance = await AppState.NfaClient.GetBalanceAsync(key);
            if (generation != _nfaCheckGeneration)
            {
                return;
            }

            if (saveIfUsable)
            {
                AppState.SaveNfaApiKey(key);
                NfaApiKeyBox.Password = "";
            }

            SetNfaKeyState(NfaKeyState.Valid, Loc.Tf("Nfa_Settings_Valid_Format", NfaPubClient.FormatEur(balance)));
        }
        catch (NfaApiException ex) when (ex.Code == "E1001")
        {
            if (generation == _nfaCheckGeneration)
            {
                SetNfaKeyState(NfaKeyState.Invalid, ex.Message);
            }
        }
        catch (NfaApiException ex)
        {
            if (generation != _nfaCheckGeneration)
            {
                return;
            }

            if (saveIfUsable)
            {
                AppState.SaveNfaApiKey(key);
                NfaApiKeyBox.Password = "";
            }

            SetNfaKeyState(NfaKeyState.Warning, Loc.Tf("Nfa_Settings_Saved_Unchecked_Format", ex.Message));
        }
        finally
        {
            if (generation == _nfaCheckGeneration)
            {
                NfaSaveKeyButton.IsEnabled = true;
                NfaRemoveKeyButton.IsEnabled = AppState.HasNfaApiKey;
                NfaApiKeyBox.PlaceholderText = Loc.T(AppState.HasNfaApiKey ? "Nfa_Settings_KeySavedPlaceholder" : "Nfa_Settings_KeyPlaceholder");
            }
        }
    }

    private void NfaRemoveKeyButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.SaveNfaApiKey(null);
        NfaApiKeyBox.Password = "";
        RefreshNfaKeyState(verify: false);
        AppState.ShowStatus(Loc.T("Nfa_Settings_Removed"), InfoBarSeverity.Informational);
    }

    private async void NfaGetKeyButton_Click(object sender, RoutedEventArgs e)
    {
        await AppState.OpenUrlAsync("https://www.nfa.pub/account");
    }

    private void SetNfaKeyState(NfaKeyState state, string message)
    {
        NfaKeyStateText.Text = message;
        (NfaKeyStateIcon.Glyph, var brushKey) = state switch
        {
            NfaKeyState.Valid => ("\uE73E", "SystemFillColorSuccessBrush"),
            NfaKeyState.Warning => ("\uE7BA", "SystemFillColorCautionBrush"),
            NfaKeyState.Invalid => ("\uEA39", "SystemFillColorCriticalBrush"),
            NfaKeyState.Checking => ("\uE895", "TextFillColorSecondaryBrush"),
            _ => ("\uE946", "TextFillColorSecondaryBrush"),
        };

        if (Application.Current.Resources.TryGetValue(brushKey, out var brush) && brush is Microsoft.UI.Xaml.Media.Brush themed)
        {
            NfaKeyStateIcon.Foreground = themed;
        }
    }

    /// <summary>Manually change the Steam install folder used for sign-in (picks which one to use when several Steam installs exist). Picker + validation + persistence all live in the coordinator.</summary>
    private async void ChangeSteamPathButton_Click(object sender, RoutedEventArgs e)
    {
        if (await SteamPathCoordinator.PickAndPersistManuallyAsync())
        {
            UpdateSteamPathText();
            AppState.ShowStatus(Loc.T("Settings_SteamPath_Changed"), InfoBarSeverity.Success);
        }
    }

    // ---------- CS2 settings sync: source account + force push on login + push now ----------

    /// <summary>Source account dropdown option: display text (nickname (Steam64)) + search text (nickname/account name/Steam64/history note).</summary>
    private sealed record Cs2SourceOption(string SteamId64, string Display, string SearchText);

    private List<Cs2SourceOption> _cs2SourceOptions = [];

    // Local copy of the saved source account SteamID64, so code does not keep loading settings to work out "who is selected".
    private string? _cs2SourceSteamId;

    // Refresh generation: when a language switch / repeated refresh clicks / navigation races run several scans at once, only the last one started is applied.
    private int _cs2RefreshGen;

    // Whether the search box (inner TextBox) has focus: if the user is typing when a refresh finishes, leave text/options alone so they are not interrupted.
    private bool _cs2SourceBoxFocused;

    private async void RefreshCs2SyncSources()
    {
        // Resolving the userdata folder + scanning source accounts + offline name lookup all run on a background thread: a large userdata / slow disk /
        // a localconfig.vdf of several MB never stalls navigation to the settings page.
        // The userdata path goes through ResolvePathsOrThrow (with auto-detect fallback), the same as "Push now",
        // fixing the mismatch where "the dropdown is always empty without a persisted Steam path, yet Push now works".
        var gen = ++_cs2RefreshGen;
        var (sources, names) = await Task.Run(() =>
        {
            try
            {
                var paths = SteamPathCoordinator.ResolvePathsOrThrow();
                var scanned = AppState.Cs2CloudService.EnumerateSources(paths.UserdataPath);
                return (scanned, SteamAccountNameService.BuildOfflineNames(paths, scanned));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Failed to scan CS2 settings source accounts: {ex.Message}");
                return ((IReadOnlyList<Cs2SettingsSource>)Array.Empty<Cs2SettingsSource>(),
                    (IReadOnlyDictionary<string, OfflineAccountName>)new Dictionary<string, OfflineAccountName>());
            }
        });

        if (gen != _cs2RefreshGen)
        {
            return;
        }

        _syncing = true;
        try
        {
            _cs2SourceOptions = BuildCs2SourceOptions(sources, names);

            var settings = AppState.SettingsService.Load();
            Cs2SyncToggle.IsOn = settings.Cs2SyncOnLogin;
            _cs2SourceSteamId = settings.Cs2SyncSourceSteamId;

            // While typing, leave text/options alone: the next keystroke refilters with the new data, and losing focus restores the canonical text.
            if (!_cs2SourceBoxFocused)
            {
                Cs2SyncSourceBox.ItemsSource = FilterCs2SourceDisplays(null);
                Cs2SyncSourceBox.Text = Cs2SourceSelectedDisplay();
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Build and sort the options: named ones first, by name; Steam64-only ones after, by number.</summary>
    private static List<Cs2SourceOption> BuildCs2SourceOptions(
        IReadOnlyList<Cs2SettingsSource> sources,
        IReadOnlyDictionary<string, OfflineAccountName> names)
    {
        var options = new List<Cs2SourceOption>(sources.Count);
        foreach (var source in sources)
        {
            // Display name priority: app history (including nicknames refreshed online) > offline lookup from this PC's Steam files.
            var history = AppState.HistoryAccounts.FirstOrDefault(item =>
                string.Equals(item.SteamId, source.SteamId64, StringComparison.OrdinalIgnoreCase));
            names.TryGetValue(source.SteamId64, out var offline);

            var persona = FirstNonEmpty(history?.PersonaName, offline?.PersonaName);
            var accountName = FirstNonEmpty(history?.AccountName, offline?.AccountName);
            var name = persona ?? accountName;
            // Some data sources store the Steam64 itself as the name; showing "X (X)" is just noise, so treat it as unnamed.
            if (string.Equals(name, source.SteamId64, StringComparison.OrdinalIgnoreCase))
            {
                name = null;
            }

            var display = name is null
                ? source.SteamId64
                : Loc.Tf("Settings_Cs2Sync_SourceItem_Format", name, source.SteamId64);

            // Search covers nickname, login account name, Steam64 and history note; the display text only holds the nickname so the dropdown stays short.
            var searchText = string.Join(' ',
                new[] { persona, accountName, source.SteamId64, history?.Note }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));

            options.Add(new Cs2SourceOption(source.SteamId64, display, searchText));
        }

        return options
            .OrderBy(option => option.Display == option.SteamId64 ? 1 : 0)
            .ThenBy(option => option.Display, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : null;

    /// <summary>Display text of the saved source; falls back to the raw Steam64 when the source folder is gone, empty when nothing is selected.</summary>
    private string Cs2SourceSelectedDisplay()
    {
        if (string.IsNullOrWhiteSpace(_cs2SourceSteamId))
        {
            return string.Empty;
        }

        var option = _cs2SourceOptions.FirstOrDefault(item =>
            string.Equals(item.SteamId64, _cs2SourceSteamId, StringComparison.OrdinalIgnoreCase));
        return option?.Display ?? _cs2SourceSteamId;
    }

    private List<string> FilterCs2SourceDisplays(string? query)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        return _cs2SourceOptions
            .Where(option => trimmed.Length == 0 ||
                option.SearchText.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .Select(option => option.Display)
            .ToList();
    }

    /// <summary>Set an option as the current source: normalize the text and persist it (picking the same account again does not write to disk again).</summary>
    private void CommitCs2Source(string display)
    {
        var option = _cs2SourceOptions.FirstOrDefault(item =>
            string.Equals(item.Display, display, StringComparison.Ordinal));
        if (option is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            Cs2SyncSourceBox.Text = option.Display;
        }
        finally
        {
            _syncing = false;
        }

        if (string.Equals(option.SteamId64, _cs2SourceSteamId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _cs2SourceSteamId = option.SteamId64;
        var settings = AppState.SettingsService.Load();
        settings.Cs2SyncSourceSteamId = option.SteamId64;
        AppState.SettingsService.Save(settings);
    }

    private void Cs2SyncSourceBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only react to user keystrokes; TextChanged fired by code assignment/selection fill-in does not reopen the options.
        if (_syncing || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        sender.ItemsSource = FilterCs2SourceDisplays(sender.Text);
    }

    private void Cs2SyncSourceBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string chosen)
        {
            CommitCs2Source(chosen);
            return;
        }

        // Plain Enter: if the text exactly matches an option, or a non-empty search term filters down to one option, treat that option as selected.
        // Enter on empty text does not auto-select (on a single-account PC that would mean "select with no input"), it only opens all options.
        var exact = _cs2SourceOptions.FirstOrDefault(option =>
            string.Equals(option.Display, args.QueryText?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            CommitCs2Source(exact.Display);
            return;
        }

        var matches = FilterCs2SourceDisplays(args.QueryText);
        if (matches.Count == 1 && !string.IsNullOrWhiteSpace(args.QueryText))
        {
            CommitCs2Source(matches[0]);
            return;
        }

        sender.ItemsSource = matches;
    }

    private void Cs2SyncSourceBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _cs2SourceBoxFocused = true;

        // Focus opens all options, keeping the old ComboBox "click to open and pick" feel.
        Cs2SyncSourceBox.ItemsSource = FilterCs2SourceDisplays(null);
        if (_cs2SourceOptions.Count > 0)
        {
            Cs2SyncSourceBox.IsSuggestionListOpen = true;
        }
    }

    private void Cs2SyncSourceBox_LostFocus(object sender, RoutedEventArgs e)
    {
        _cs2SourceBoxFocused = false;

        if (_syncing)
        {
            return;
        }

        // Committing only goes through QuerySubmitted (Enter/clicking an option). Losing focus always reverts to the display text of the saved selection,
        // matching the old ComboBox light cancel behavior: an option only previewed with arrow keys or a half-typed search term does not count as a selection,
        // otherwise "glance and click away" would silently write the highlighted item into settings, and later pushes would push the wrong account.
        _syncing = true;
        try
        {
            Cs2SyncSourceBox.Text = Cs2SourceSelectedDisplay();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Cs2SyncToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        var settings = AppState.SettingsService.Load();
        settings.Cs2SyncOnLogin = Cs2SyncToggle.IsOn;
        AppState.SettingsService.Save(settings);
    }

    private void Cs2SyncRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshCs2SyncSources();
    }

    private async void Cs2SyncPushNowButton_Click(object sender, RoutedEventArgs e)
    {
        var source = AppState.SettingsService.Load().Cs2SyncSourceSteamId;
        if (string.IsNullOrWhiteSpace(source))
        {
            AppState.ShowStatus(Loc.T("Cs2Cloud_Error_NoSourceSelected"), InfoBarSeverity.Error);
            return;
        }

        AppState.ShowStatus(Loc.T("Cs2Cloud_Progress_Pushing"), InfoBarSeverity.Informational);
        // Disable the button while pushing so repeated clicks do not queue several pushes in a row.
        Cs2SyncPushNowButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                try
                {
                    var paths = SteamPathCoordinator.ResolvePathsOrThrow();
                    return AppState.Cs2CloudService.PushSourceNow(paths, source);
                }
                catch (Exception ex)
                {
                    return new Cs2CloudPushResult(false, 0, ex.Message);
                }
            });

            var severity = !result.Ok
                ? InfoBarSeverity.Error
                : result.AccountCloudDisabled ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            AppState.ShowStatus(Cs2CloudService.DescribeResult(result), severity);
        }
        finally
        {
            Cs2SyncPushNowButton.IsEnabled = true;
        }
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = AppState.SettingsService.AppFolderPath;
        try
        {
            Directory.CreateDirectory(folder);
            // explorer.exe takes a folder path as an argument and opens File Explorer directly; more reliable than ShellExecute on a folder.
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to open the data folder.", ex);
            AppState.ShowStatus(Loc.T("Settings_Data_OpenFail"), InfoBarSeverity.Error);
        }
    }
}
