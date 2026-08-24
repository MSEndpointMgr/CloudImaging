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

        // Apply the theme once the window handle exists. WinPE may not expose the
        // personalization registry keys (and access differs under elevation), so any
        // failure falls back to Dark and never blocks startup (theme is cosmetic).
        Loaded += (_, _) => ApplySystemThemeWithDarkFallback();
    }

    /// <summary>
    /// Makes the app adhere to the OS light/dark setting. When the system theme
    /// cannot be determined — e.g. under WinPE, where the HKCU personalization keys
    /// are absent — the app defaults to Dark. On a real desktop the watcher then
    /// tracks live OS theme changes; under WinPE the watcher is deliberately not
    /// started so a spurious change notification can't flip the app back to Light.
    /// </summary>
    private void ApplySystemThemeWithDarkFallback()
    {
        SystemTheme systemTheme;
        try
        {
            systemTheme = ApplicationThemeManager.GetSystemTheme();
        }
        catch
        {
            systemTheme = SystemTheme.Unknown;
        }

        switch (systemTheme)
        {
            case SystemTheme.Light:
                SafeApply(ApplicationTheme.Light);
                SafeWatch();
                break;

            // Dark and the dark-family default Windows themes.
            case SystemTheme.Dark:
            case SystemTheme.CapturedMotion:
            case SystemTheme.Glow:
            case SystemTheme.Sunrise:
                SafeApply(ApplicationTheme.Dark);
                SafeWatch();
                break;

            // Unknown / unreadable (WinPE) → Dark default, watcher left off.
            default:
                SafeApply(ApplicationTheme.Dark);
                break;
        }
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

    private void SafeWatch()
    {
        try
        {
            SystemThemeWatcher.Watch(this);
        }
        catch
        {
            // Following live OS theme changes is a cosmetic enhancement; ignore.
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
