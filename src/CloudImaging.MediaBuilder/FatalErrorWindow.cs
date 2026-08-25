using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CloudImaging.MediaBuilder;

/// <summary>
/// Fatal-error dialog shown by <see cref="App.ReportFatal"/> for any unhandled startup/runtime
/// exception.
///
/// Built entirely in code with hardcoded colors/fonts — no XAML, no DynamicResource/WPF-UI
/// theme dependency — so it can still render even if the very failure being reported is a
/// resource-dictionary/theme load problem. Mirrors the accent-strip design used by the Prepare
/// USB Device result dialog and the Client's Results screen (thin colour strip, plain title +
/// selectable detail text, single action button), using plain WPF primitives instead of WPF-UI
/// controls for that same robustness reason.
/// </summary>
internal sealed class FatalErrorWindow : Window
{
    public FatalErrorWindow(Exception? ex)
    {
        Title = "Cloud Imaging Media Builder";
        Width = 560;
        Height = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ShowInTaskbar = true;
        Topmost = true;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Accent strip — critical/red, matching SystemFillColorCriticalBrush's typical value.
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)) });

        var titlePanel = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };
        titlePanel.Children.Add(new TextBlock
        {
            Text = "Cloud Imaging Media Builder failed to start",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = ex?.Message ?? "An unknown error occurred.",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xC7)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        Grid.SetRow(titlePanel, 1);
        root.Children.Add(titlePanel);

        // Full exception detail, read-only/selectable so it can be copied into a bug report.
        var detailBox = new TextBox
        {
            Text = ex?.ToString() ?? string.Empty,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            IsUndoEnabled = false,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xC7, 0xC7)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(24, 0, 24, 12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(detailBox, 2);
        root.Children.Add(detailBox);

        var closeButton = new Button
        {
            Content = "Close",
            Width = 96,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(24, 0, 24, 20),
            Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x45)),
        };
        closeButton.Click += (_, _) => Close();
        Grid.SetRow(closeButton, 3);
        root.Children.Add(closeButton);

        Content = root;
    }
}
