using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace CloudImaging.MediaBuilder.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public void NavigateTo(Page page)
    {
        NavigationFrame.Navigate(page);
    }
}
