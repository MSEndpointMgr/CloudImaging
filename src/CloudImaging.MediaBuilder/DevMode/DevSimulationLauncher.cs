#if DEV_SIMULATION
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CloudImaging.MediaBuilder.DevMode;

/// <summary>
/// DEVELOPER-ONLY simulation launcher.
///
/// A floating tool window that lets a developer jump directly to any view and exercise
/// every screen of the Media Builder without a real Entra ID sign-in or backend — useful
/// while building the app. It bypasses the sign-in gate purely for local development.
///
/// SAFETY: This entire file is wrapped in <c>#if DEV_SIMULATION</c>. The
/// <c>DEV_SIMULATION</c> constant is defined ONLY for Debug builds (see the
/// <c>&lt;DefineConstants&gt;</c> condition in <c>CloudImaging.MediaBuilder.csproj</c>).
/// Release builds — which are what every shipped/published artifact uses
/// (<c>-c Release</c> in <c>.github/workflows/release.yml</c>) — compile this file to
/// nothing, so the dev navigator can never be surfaced in a released application.
/// </summary>
internal sealed class DevSimulationLauncher : Window
{
    public DevSimulationLauncher(
        Window owner,
        Action onSignInView,
        Action onOperationSelectionView,
        Action onGenerateBootImageView,
        Action onPrepareUsbView)
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
            Text                = "DEV SIMULATION",
            FontWeight          = FontWeights.Bold,
            FontSize            = 14,
            Foreground          = new SolidColorBrush(Color.FromRgb(0xF9, 0xD7, 0x0B)),
            Margin              = new Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new TextBlock
        {
            Text                = "Debug-only navigator. Excluded from Release builds. Jump to any view and bypass sign-in.",
            TextWrapping        = TextWrapping.Wrap,
            FontSize            = 11,
            Foreground          = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
            Margin              = new Thickness(0, 0, 0, 14),
        });

        // MANDATORY: every navigable view MUST have a button here. When a new view is added
        // to the app, add its NavButton below and wire the matching Action in
        // App.ShowDevSimulationLauncher — in the SAME change. Keep this list complete.
        panel.Children.Add(NavButton("1 · Sign In view", onSignInView));
        panel.Children.Add(NavButton("2 · Operation Selection", onOperationSelectionView));
        panel.Children.Add(NavButton("3 · Generate Boot Image", onGenerateBootImageView));
        panel.Children.Add(NavButton("4 · Prepare USB Device", onPrepareUsbView));

        Content = panel;
    }

    private static Button NavButton(string label, Action onClick)
    {
        var button = new Button
        {
            Content             = label,
            Height              = 34,
            Margin              = new Thickness(0, 0, 0, 8),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding             = new Thickness(10, 0, 10, 0),
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
#endif
