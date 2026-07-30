#if DEV_SIMULATION
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CloudImaging.Client.DevMode;

/// <summary>
/// DEVELOPER-ONLY simulation launcher.
///
/// A floating tool window that lets a developer jump directly to any view and exercise
/// every screen of the Cloud Imaging Client without a real device, mTLS boot-media
/// certificate, or a reachable Device Gateway API — useful while building the app.
///
/// SAFETY: This entire file is wrapped in <c>#if DEV_SIMULATION</c>. The
/// <c>DEV_SIMULATION</c> constant is defined ONLY for Debug builds (see the
/// <c>&lt;DefineConstants&gt;</c> condition in <c>CloudImaging.Client.csproj</c>).
/// Release builds — which are what every shipped/published artifact uses
/// (<c>-c Release</c> in <c>.github/workflows/release.yml</c>) — compile this file to
/// nothing, so the dev navigator can never be surfaced in a released application.
///
/// Mirrors the CloudImaging.MediaBuilder DevSimulationLauncher pattern.
/// </summary>
internal sealed class DevSimulationLauncher : Window
{
    public DevSimulationLauncher(
        Window owner,
        Action onOperationSelectionView,
        Action onSessionInitView,
        Action onProgressView,
        Action onResultsSuccessView,
        Action onResultsFailureView,
        Action onResultsNotAuthorizedView)
    {
        Owner                 = owner;
        Title                 = "DEV SIMULATION";
        Width                 = 260;
        SizeToContent         = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left                  = 24;
        Top                   = 80;
        Topmost               = true;
        ShowInTaskbar         = false;
        ResizeMode            = ResizeMode.NoResize;
        Background            = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

        var panel = new StackPanel { Margin = new Thickness(16) };

        panel.Children.Add(new TextBlock
        {
            Text       = "DEV SIMULATION",
            FontWeight = FontWeights.Bold,
            FontSize   = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF9, 0xD7, 0x0B)),
            Margin     = new Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new TextBlock
        {
            Text         = "Debug-only navigator. Excluded from Release builds. Jump to any view without a device, boot-media cert, or Device Gateway.",
            TextWrapping = TextWrapping.Wrap,
            FontSize     = 11,
            Foreground   = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
            Margin       = new Thickness(0, 0, 0, 14),
        });

        panel.Children.Add(NavButton("1 · Operation Selection", onOperationSelectionView));
        panel.Children.Add(NavButton("2 · Session Init (awaiting operator)", onSessionInitView));
        panel.Children.Add(NavButton("3 · Imaging Progress", onProgressView));
        panel.Children.Add(NavButton("4 · Results — Success", onResultsSuccessView));
        panel.Children.Add(NavButton("5 · Results — Failure", onResultsFailureView));
        panel.Children.Add(NavButton("6 · Results — Not Authorized", onResultsNotAuthorizedView));

        Content = panel;
    }

    private static Button NavButton(string label, Action onClick)
    {
        var button = new Button
        {
            Content                    = label,
            Height                     = 34,
            Margin                     = new Thickness(0, 0, 0, 8),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding                    = new Thickness(10, 0, 10, 0),
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
#endif
