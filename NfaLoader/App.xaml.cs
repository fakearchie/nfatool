using Microsoft.UI.Xaml;
using NfaLoader.Localization;
using NfaLoader.Services;

namespace NfaLoader;

public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// The theme the UI is actually showing. The theme is set on the window's root element (see MainWindow.ApplyTheme),
    /// and Application.RequestedTheme always holds the startup value, so read ActualTheme from the root element.
    /// </summary>
    internal static ElementTheme ActualTheme =>
        Current is App app && app._window?.Content is FrameworkElement root
            ? root.ActualTheme
            : ElementTheme.Default;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Pick the UI language before any window or page is built, so the first frame renders in the chosen language.
        Loc.Initialize(AppState.SettingsService);
        AppState.UpdateService.SetProxySite(AppState.SettingsService.Load().UpdateProxySite);

        _window = new MainWindow();
        _window.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // Try to write to disk before crashing, to help debug field issues in AOT builds.
        try
        {
            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "nfa.pub Loader");
            Directory.CreateDirectory(logFolder);
            File.AppendAllText(
                Path.Combine(logFolder, "crash.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}");
        }
        catch
        {
            // A failed log write does not stop the exception from propagating.
        }
    }
}
