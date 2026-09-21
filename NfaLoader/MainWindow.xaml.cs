using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NfaLoader.Localization;
using NfaLoader.Models;
using NfaLoader.Pages;
using NfaLoader.Services;
using Windows.Graphics;

namespace NfaLoader;

public sealed partial class MainWindow : Window
{
    private const int InitialWindowWidth = 1280;
    private const int InitialWindowHeight = 860;
    private const int MinWindowWidth = 1180;
    private const int MinWindowHeight = 780;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const nuint WindowSubclassId = 1;

    private static nint s_hwnd;

    // Informational/Success messages close on their own after a few seconds; Warning/Error stay until replaced or closed by the user.
    private static readonly TimeSpan StatusAutoDismissDelay = TimeSpan.FromSeconds(6);
    private readonly DispatcherQueueTimer _statusDismissTimer;

    public static MainWindow? Instance { get; private set; }

    /// <summary>Main window handle, used by WinRT interop such as file/folder pickers (InitializeWithWindow); set in ConfigureWindowSize.</summary>
    public static nint Hwnd => s_hwnd;

    public MainWindow()
    {
        Instance = this;

        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SetWindowIcon();
        TitleVersionText.Text = $"v{GitHubUpdateService.CurrentVersion}";

        ApplyTheme(ParseTheme(AppState.SettingsService.Load().Theme));
        RefreshNavText();
        Loc.LanguageChanged += RefreshNavText;

        _statusDismissTimer = DispatcherQueue.CreateTimer();
        _statusDismissTimer.Interval = StatusAutoDismissDelay;
        _statusDismissTimer.IsRepeating = false;
        _statusDismissTimer.Tick += (_, _) => StatusInfoBar.IsOpen = false;

        AppState.StatusReporter = ShowStatus;
        AppState.BusyChanged += OnBusyChanged;
        AppState.UpdateStateChanged += RefreshUpdateBadge;
        RefreshUpdateBadge();

        ConfigureWindowSize();

        // Preload account history; the sign-in page reuses avatars/profile data from this cache.
        AppState.ReloadHistory();
        RootNavigationView.SelectedItem = LoginNavItem;

        // Resolve and persist the Steam install path on first launch (reused for every sign-in after that, no detection each time).
        // Runs once the content is in the visual tree (XamlRoot ready), since a dialog is needed if detection fails.
        RootNavigationView.Loaded += OnRootNavigationViewLoaded;

        _ = AppState.CheckForUpdatesAsync(isAutomatic: true);
    }

    // Resolve the Steam path at startup: persist it quietly on success; if auto-detection fails, show a dialog so the user picks the folder that contains steam.exe.
    private async void OnRootNavigationViewLoaded(object sender, RoutedEventArgs e)
    {
        RootNavigationView.Loaded -= OnRootNavigationViewLoaded;
        try
        {
            await SteamPathCoordinator.EnsureResolvedAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to resolve the Steam install path at startup.", ex);
        }
    }

    public void ShowStatus(string message, InfoBarSeverity severity)
    {
        // Status can come from a background thread (such as CS2 cloud push progress after sign-in), so always marshal it to the UI thread.
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ShowStatus(message, severity));
            return;
        }

        StatusInfoBar.Message = message;
        StatusInfoBar.Severity = severity;
        StatusInfoBar.IsOpen = true;

        // Warnings and errors stay up longer, but everything closes on its own. Before, they stayed open
        // forever and followed the user onto every page.
        _statusDismissTimer.Stop();
        _statusDismissTimer.Interval = severity is InfoBarSeverity.Informational or InfoBarSeverity.Success
            ? StatusAutoDismissDelay
            : TimeSpan.FromSeconds(12);
        _statusDismissTimer.Start();
    }

    /// <summary>Shows a red dot on the "About" nav item when a new version is out, replacing the update banner that used to sit at the bottom.</summary>
    private void RefreshUpdateBadge()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshUpdateBadge);
            return;
        }

        AboutNavItem.InfoBadge = AppState.LatestUpdate?.IsUpdateAvailable == true
            ? new InfoBadge()
            : null;
    }

    /// <summary>History page "Load into sign-in page": switches to the sign-in page and fills in the account.</summary>
    public void LoadAccountIntoLogin(SteamAccountHistoryItem account)
    {
        RootNavigationView.SelectedItem = LoginNavItem;
        AppState.LoginPage?.LoadHistoryAccount(account);
    }

    public void ShowSettings()
    {
        RootNavigationView.SelectedItem = SettingsNavItem;
    }

    private void RootNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string pageName)
        {
            NavigateTo(pageName);
        }
    }

    private void NavigateTo(string pageName)
    {
        var pageType = pageName switch
        {
            "history" => typeof(HistoryPage),
            "cachedAccounts" => typeof(CachedAccountsPage),
            "loadout" => typeof(LoadoutPage),
            "personalization" => typeof(PersonalizationPage),
            "settings" => typeof(SettingsPage),
            "about" => typeof(AboutPage),
            _ => typeof(LoginPage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            // A message belongs to the page it came from.
            StatusInfoBar.IsOpen = false;
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
            ContentFrame.BackStack.Clear();
        }
    }

    private void OnBusyChanged(bool isBusy)
    {
        BusyRing.IsActive = isBusy;
        BusyRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Applies the theme to the content root (unpackaged, Application.RequestedTheme cannot be changed after startup, so the root element's RequestedTheme is used).</summary>
    public void ApplyTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
    }

    private static ElementTheme ParseTheme(string theme) => theme switch
    {
        "Light" => ElementTheme.Light,
        "Dark" => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    /// <summary>Localizes the nav item text; Loc.LanguageChanged calls it again when the language changes.</summary>
    private void RefreshNavText()
    {
        LoginNavItem.Content = Loc.T("Nav_Login");
        HistoryNavItem.Content = Loc.T("Nav_History");
        CachedAccountsNavItem.Content = Loc.T("Nav_CachedAccounts");
        LoadoutNavItem.Content = Loc.T("Nav_Loadout");
        PersonalizationNavItem.Content = Loc.T("Nav_Personalization");
        SettingsNavItem.Content = Loc.T("Nav_Settings");
        AboutNavItem.Content = Loc.T("Nav_About");
    }

    private void SetWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
    }

    private unsafe void ConfigureWindowSize()
    {
        s_hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(s_hwnd) / 96.0;

        AppWindow.Resize(new SizeInt32(
            (int)Math.Ceiling(InitialWindowWidth * scale),
            (int)Math.Ceiling(InitialWindowHeight * scale)));

        // Subclass WM_GETMINMAXINFO to compute the minimum size from the current DPI each time,
        // because OverlappedPresenter.PreferredMinimum* (physical pixels fixed at startup) breaks across monitors / DPI changes.
        SetWindowSubclass(s_hwnd, &SubclassProc, WindowSubclassId, 0);
        Closed += OnClosed;
    }

    private unsafe void OnClosed(object sender, WindowEventArgs args)
    {
        RemoveWindowSubclass(s_hwnd, &SubclassProc, WindowSubclassId);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe nint SubclassProc(
        nint hWnd,
        uint message,
        nint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == WmGetMinMaxInfo)
        {
            var scale = GetDpiForWindow(hWnd) / 96.0;
            var info = (MinMaxInfo*)lParam;
            info->MinTrackSize.X = (int)Math.Ceiling(MinWindowWidth * scale);
            info->MinTrackSize.Y = (int)Math.Ceiling(MinWindowHeight * scale);
            return 0;
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetWindowSubclass(
        nint hWnd,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> callback,
        nuint subclassId,
        nuint referenceData);

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool RemoveWindowSubclass(
        nint hWnd,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> callback,
        nuint subclassId);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hWnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }
}
