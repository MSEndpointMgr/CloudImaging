using System.Windows;
using System.Windows.Controls;

namespace CloudImaging.Client.Views;

/// <summary>
/// Imaging progress screen. Hosted by <see cref="MainWindow"/>'s navigation frame.
/// </summary>
public partial class ProgressView : Page
{
    public ProgressView()
    {
        InitializeComponent();
        ActivityLogTextBox.TextChanged += (_, _) => ActivityLogTextBox.ScrollToEnd();
    }

    /// <summary>Opens the read-only local log viewer (FR-066) — see <see cref="LogViewerWindow"/>.</summary>
    private void ViewLogButton_Click(object sender, RoutedEventArgs e)
    {
        DialogHost.ShowDimmed(new LogViewerWindow(), Window.GetWindow(this));
    }
}
