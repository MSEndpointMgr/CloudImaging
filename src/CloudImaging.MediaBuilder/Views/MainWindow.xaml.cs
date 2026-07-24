using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace CloudImaging.MediaBuilder.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        // Follow the OS light/dark theme. Matches the working reference app, which
        // calls this in the constructor. Guarded so a failure (e.g. under elevation)
        // never blocks startup — theme following is cosmetic.
        try
        {
            Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        }
        catch (Exception)
        {
            // Keep the default theme.
        }
    }

    /// <summary>Swaps the hosted view (a UserControl) shown below the title bar.</summary>
    public void NavigateTo(System.Windows.Controls.Control view)
    {
        ContentHost.Content = view;
    }
}
