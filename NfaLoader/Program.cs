using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NfaLoader.Services;

namespace NfaLoader;

/// <summary>
/// Custom entry point (the csproj defines DISABLE_XAML_GENERATED_MAIN):
/// when started with <see cref="Cs2CloudPushWorker.CommandLineSwitch"/> it runs as the "CS2 cloud push helper process",
/// never initializes XAML or a window, and exits once the push is done; otherwise it starts the main UI the same way the WinUI generated entry point does.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == Cs2CloudPushWorker.CommandLineSwitch)
        {
            return Cs2CloudPushWorker.Run(args);
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
