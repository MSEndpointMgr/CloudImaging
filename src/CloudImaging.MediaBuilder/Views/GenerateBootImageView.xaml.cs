using System.Windows.Controls;

namespace CloudImaging.MediaBuilder.Views;

/// <summary>
/// Generate boot image workflow screen. Hosted by <see cref="MainWindow"/>
/// via its content host.
/// </summary>
public partial class GenerateBootImageView : UserControl
{
    /// <summary>Initializes a new instance of <see cref="GenerateBootImageView"/>.</summary>
    public GenerateBootImageView()
    {
        InitializeComponent();
        // Keep the command/output log scrolled to the newest line as it streams in.
        LogTextBox.TextChanged += (_, _) => LogTextBox.ScrollToEnd();
    }
}
