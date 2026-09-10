using System.Windows;

namespace CloudImaging.Client.Views;

/// <summary>
/// Opens a <see cref="Window"/> modally (owned by, and centered over, <paramref name="owner"/>),
/// dimming <see cref="MainWindow"/> behind it for the duration.
/// </summary>
/// <remarks>
/// Every modal support-tool window in this app (<see cref="LogViewerWindow"/>,
/// <see cref="WifiConnectionWindow"/>) is opened via <c>ShowDialog()</c> so input to the owner is
/// already blocked at the Win32 level — but WinPE has no DWM composition, so there's no automatic
/// drop shadow or dimming to tell the two windows apart visually (see
/// <c>MainWindow.xaml</c>'s <c>DimOverlay</c> remarks). Routing every such call through here
/// means the dim toggle can't be forgotten at a call site, and stays paired via <c>finally</c>
/// even if the child window throws while opening.
/// </remarks>
internal static class DialogHost
{
    public static void ShowDimmed(Window child, Window? owner)
    {
        child.Owner = owner;

        MainWindow? mainWindow = owner as MainWindow ?? Application.Current.MainWindow as MainWindow;
        mainWindow?.SetDimmed(true);
        try
        {
            child.ShowDialog();
        }
        finally
        {
            mainWindow?.SetDimmed(false);
        }
    }
}
