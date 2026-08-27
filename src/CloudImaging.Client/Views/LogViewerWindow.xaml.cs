using System.IO;
using System.Windows;
using CloudImaging.Client.Logging;
using Wpf.Ui.Controls;

namespace CloudImaging.Client.Views;

/// <summary>
/// Read-only viewer for the Client's local rolling diagnostic log file (FR-066). Opened from the
/// always-available "View Log" button on <see cref="MainWindow"/> so a technician can inspect
/// every request/step logged so far without needing Explorer/Notepad, which WinPE does not
/// provide. Purely local: it only reads the file Serilog is already writing to
/// (<see cref="LoggingConfiguration.CurrentLogFilePath"/>) and never uploads or transmits it.
/// </summary>
public partial class LogViewerWindow : FluentWindow
{
    /// <summary>
    /// Upper bound on how much of the log file is loaded into the view, so the window stays
    /// responsive even once the rolling file approaches its 10 MB cap — shows the most recent
    /// (and most relevant) activity rather than the oldest.
    /// </summary>
    private const int MaxTailBytes = 512 * 1024; // 512 KB

    public LogViewerWindow()
    {
        InitializeComponent();

        // FluentWindow's own OnSourceInitialized unconditionally forces WindowStyle back to
        // SingleBorderWindow and only hides the resulting native caption via a WindowChrome hack
        // that's gated on DWM composition being enabled — which WinPE never has. Reasserting
        // WindowStyle = None alone (even though SourceInitialized fires after that base logic
        // runs) was verified NOT to reliably clear the native caption in real WinPE testing, so
        // fall back to stripping the WS_CAPTION/WS_SYSMENU bits directly on the HWND — see
        // NativeWindowChromeFix for the full explanation.
        SourceInitialized += (_, _) =>
        {
            WindowStyle = WindowStyle.None;
            NativeWindowChromeFix.RemoveNativeCaption(this);
        };

        LoadLog();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadLog();

    private void CopyPathButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LoggingConfiguration.CurrentLogFilePath);
        }
        catch
        {
            // Clipboard access can fail under WinPE/remote sessions — non-critical, ignore.
        }
    }

    private void LoadLog()
    {
        var path = LoggingConfiguration.CurrentLogFilePath;
        LogPathText.Text = path;

        try
        {
            if (!File.Exists(path))
            {
                LogTextBox.Text = $"No log file found yet at:\n{path}";
                return;
            }

            // FileShare.ReadWrite: Serilog holds this file open for writing (shared: true in
            // LoggingConfiguration.CreateLogger), so it must be opened without an exclusive lock.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            string content;
            if (stream.Length > MaxTailBytes)
            {
                stream.Seek(-MaxTailBytes, SeekOrigin.End);
                reader.DiscardBufferedData();
                content = $"[\u2026truncated \u2014 showing the most recent {MaxTailBytes / 1024} KB\u2026]\n\n{reader.ReadToEnd()}";
            }
            else
            {
                content = reader.ReadToEnd();
            }

            LogTextBox.Text = content;
            LogTextBox.CaretIndex = LogTextBox.Text.Length;
            LogTextBox.ScrollToEnd();
        }
        catch (Exception ex)
        {
            LogTextBox.Text = $"Could not read log file:\n{path}\n\n{ex.Message}";
        }
    }
}
