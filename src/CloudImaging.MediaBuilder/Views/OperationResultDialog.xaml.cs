using System.Windows.Controls;

namespace CloudImaging.MediaBuilder.Views;

/// <summary>Shared success/failure/cancelled result modal; binds to the host view model.</summary>
public partial class OperationResultDialog : UserControl
{
    /// <summary>Creates the dialog.</summary>
    public OperationResultDialog()
    {
        InitializeComponent();
    }
}
