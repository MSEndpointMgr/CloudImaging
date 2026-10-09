using System.Windows.Controls;

namespace CloudImaging.Client.Views;

/// <summary>
/// Session initialization screen. Hosted by <see cref="MainWindow"/>'s navigation frame.
/// </summary>
public partial class SessionInitView : Page
{
    /// <summary>Builds the view and loads its XAML.</summary>
    public SessionInitView()
    {
        InitializeComponent();
    }
}
