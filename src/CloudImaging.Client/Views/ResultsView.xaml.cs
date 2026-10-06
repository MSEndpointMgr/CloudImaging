using System.Windows;
using System.Windows.Controls;

namespace CloudImaging.Client.Views;

/// <summary>
/// Results / completion screen. Hosted by <see cref="MainWindow"/>'s navigation frame.
/// </summary>
public partial class ResultsView : Page
{
    public ResultsView()
    {
        InitializeComponent();
    }

    /// <summary>The blocked card has its own View Log, since the fix hints for failed checks are only in the log.</summary>
    private void ViewLogButton_Click(object sender, RoutedEventArgs e)
    {
        DialogHost.ShowDimmed(new LogViewerWindow(), Window.GetWindow(this));
    }
}
