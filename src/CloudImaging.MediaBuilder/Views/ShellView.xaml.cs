using System.Windows.Controls;

namespace CloudImaging.MediaBuilder.Views;

/// <summary>
/// Persistent NavigationView-style app shell shown once the user is signed in. Hosted by
/// <see cref="MainWindow"/> via its content host; its own content area swaps sections
/// internally (see <c>ShellViewModel</c>) so the nav pane never leaves.
/// </summary>
public partial class ShellView : UserControl
{
    /// <summary>Builds the view and loads its XAML.</summary>
    public ShellView()
    {
        InitializeComponent();
    }
}
