using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace CloudImaging.MediaBuilder.Views;

/// <summary>
/// Operation selection screen shown after sign-in. Hosted by
/// <see cref="MainWindow"/> via its content host.
/// </summary>
public partial class OperationSelectionView : UserControl
{
    /// <summary>Initializes a new instance of <see cref="OperationSelectionView"/>.</summary>
    public OperationSelectionView()
    {
        InitializeComponent();
    }

    /// <summary>Opens the ADK download link in the user's default browser.</summary>
    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
