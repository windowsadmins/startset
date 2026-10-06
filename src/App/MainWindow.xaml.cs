using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using StartSet.App.Views;
using StartSet.Infrastructure.Gui;

namespace StartSet.App;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainWindow()
    {
        InitializeComponent();

        Title = "Managed State Keeper";

        // DPI-aware sizing clamped to available screen work area
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        int targetW = (int)(1180 * scale);
        int targetH = (int)(900 * scale);
        int maxW = (int)(workArea.Width * 0.96);
        int maxH = (int)(workArea.Height * 0.96);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Min(targetW, maxW),
            Math.Min(targetH, maxH)));

        // Extend content into title bar for seamless theme-matching appearance
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Apply Mica backdrop for modern Windows 11 look
        SystemBackdrop = new MicaBackdrop();

        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();

        // Open on Prefs when asked to (the elevated relaunch from Unlock passes --prefs);
        // --tab run|logs opens another tab.
        var tab = PrefsElevation.OpensOnPrefs(args) ? "prefs" : TabArgument(args) ?? "prefs";
        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(i => (string?)i.Tag == tab) ?? NavView.MenuItems[0];
    }

    private static string? TabArgument(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, "--tab", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1].ToLowerInvariant() : null;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item)
        {
            var tag = item.Tag?.ToString();
            var pageType = tag switch
            {
                "prefs" => typeof(PrefsPage),
                "run"   => typeof(RunPage),
                "logs"  => typeof(LogsPage),
                _       => typeof(PrefsPage)
            };
            ContentFrame.Navigate(pageType);
        }
    }
}
