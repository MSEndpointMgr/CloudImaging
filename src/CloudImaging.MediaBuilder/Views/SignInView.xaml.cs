using System.Windows.Controls;

namespace CloudImaging.MediaBuilder.Views;

/// <summary>
/// Welcome / Entra ID sign-in screen (FR-050, FR-052). Hosted by
/// <see cref="MainWindow"/> via its content host.
/// </summary>
public partial class SignInView : UserControl
{
    /// <summary>Builds the view and loads its XAML.</summary>
    public SignInView()
    {
        InitializeComponent();
    }
}
