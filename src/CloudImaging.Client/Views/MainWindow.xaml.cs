using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace CloudImaging.Client.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        // FluentWindow's own OnSourceInitialized unconditionally forces WindowStyle back to
        // SingleBorderWindow and only hides the resulting native chrome via a WindowChrome hack
        // that's gated on DWM composition being enabled, which WinPE never has. Reasserting
        // WindowStyle = None alone (even though SourceInitialized fires after that base logic
        // runs) was verified NOT to reliably clear the native chrome in real WinPE testing, so
        // strip the caption/border style bits directly on the HWND and collapse the non-client
        // area outright. See NativeWindowChromeFix for the full explanation.
        SourceInitialized += (_, _) =>
        {
            WindowStyle = WindowStyle.None;
            NativeWindowChromeFix.RemoveNativeCaption(this);
        };

        // The Client only ever runs in WinPE (there is no OS light/dark setting to honor and
        // no shell to raise a live theme-change notification for a SystemThemeWatcher to
        // observe), so it always forces Dark — matching the XAML default in App.xaml — rather
        // than querying/watching the system theme. Applied once the window handle exists.
        Loaded += (_, _) => SafeApply(ApplicationTheme.Dark);
    }

    private static void SafeApply(ApplicationTheme theme)
    {
        try
        {
            ApplicationThemeManager.Apply(theme);
        }
        catch
        {
            // Keep the XAML default (Dark) if applying fails.
        }
    }

    public void NavigateTo(Page page)
    {
        NavigationFrame.Navigate(page);

        // ProgressView hosts its own "View Log" button at the bottom of its step sidebar (it
        // sits closer to hand and doesn't overlap the hero panel's progress bar/error area), so
        // the floating global button is hidden while it's the active page.
        ViewLogButton.Visibility = page is ProgressView ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Toggles the window's Topmost state (FR-051d support tools). Used by
    /// <see cref="Services.CommandPromptLauncherService"/> to drop this always-on-top window
    /// (see the XAML remarks on <c>Topmost="True"</c> above) while an interactive command
    /// prompt is open, restoring it once that process exits. Thread-safe: <see
    /// cref="System.Diagnostics.Process.Exited"/> fires on a thread-pool thread, so this
    /// marshals onto the UI thread when called from anywhere else.
    /// </summary>
    public void SetTopmost(bool topmost)
    {
        if (Dispatcher.CheckAccess())
            Topmost = topmost;
        else
            Dispatcher.Invoke(() => Topmost = topmost);
    }

    /// <summary>
    /// Opens the read-only local log viewer (FR-066) — see <see cref="LogViewerWindow"/>.
    /// Available from every screen since it's hosted on <see cref="MainWindow"/> itself rather
    /// than any individual <see cref="Page"/>.
    /// </summary>
    private void ViewLogButton_Click(object sender, RoutedEventArgs e)
    {
        new LogViewerWindow { Owner = this }.ShowDialog();
    }
}
