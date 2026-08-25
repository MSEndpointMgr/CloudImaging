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

        // FluentWindow's own OnSourceInitialized forces WindowStyle back to SingleBorderWindow
        // and only hides the resulting native caption via a WindowChrome hack that's gated on
        // DWM composition being enabled — which WinPE never has. Without composition, that
        // leaves a native "Basic theme" title bar stacked above our own ui:TitleBar. Reassert
        // None here (SourceInitialized fires after that base logic runs) to fully remove it.
        SourceInitialized += (_, _) => WindowStyle = WindowStyle.None;

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
    /// Opens the read-only local log viewer (FR-066) — see <see cref="LogViewerWindow"/>.
    /// Available from every screen since it's hosted on <see cref="MainWindow"/> itself rather
    /// than any individual <see cref="Page"/>.
    /// </summary>
    private void ViewLogButton_Click(object sender, RoutedEventArgs e)
    {
        new LogViewerWindow { Owner = this }.ShowDialog();
    }
}
