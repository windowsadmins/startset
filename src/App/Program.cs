using StartSet.Infrastructure.Gui;

namespace StartSet.App;

/// <summary>
/// Entry point. Replaces the XAML-generated Main so that the elevated, headless half of the
/// Prefs tab's Save (--apply-settings) writes machine settings and exits before WinUI starts
/// and without ever creating a window.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (SettingsPayload.IsApplyRun(args))
            return SettingsPayload.RunHeadless(args);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
