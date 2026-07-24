using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace CloudImaging.Client.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        // Apply theme watching once the window handle exists. WinPE may not expose
        // the personalization registry keys and access differs under elevation, so
        // defer to Loaded and treat failures as non-fatal (cosmetic only).
        Loaded += (_, _) =>
        {
            try
            {
                Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
            }
            catch (Exception)
            {
                // Theme watching is a cosmetic enhancement; ignore under WinPE.
            }
        };
    }

    public void NavigateTo(Page page)
    {
        NavigationFrame.Navigate(page);
    }
}
