using System.Windows.Controls;

namespace CloudImaging.Client.Views;

/// <summary>
/// Results / completion screen. Hosted by <see cref="MainWindow"/>'s navigation frame.
/// </summary>
public partial class ResultsView : Page
{
    /// <summary>Builds the view and loads its XAML.</summary>
    public ResultsView()
    {
        InitializeComponent();
    }
}
