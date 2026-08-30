using System.IO;
using System.Windows;
using System.Windows.Threading;
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
    /// responsive even if the rolling file approaches its 10 MB cap. Deliberately close to that
    /// cap (not a small fraction of it, as a 512 KB tail previously was): a single imaging session
    /// alone routinely logs several hundred KB (e.g. diskpart's quick-format progress ticker emits
    /// one line per percent), so a small cap silently discarded the start of the very session a
    /// technician opens this viewer to investigate, with no way to page back further.
    /// </summary>
    private const int MaxTailBytes = 8 * 1024 * 1024; // 8 MB

    /// <summary>How often the log file is polled for newly-appended content (CMTrace-style tailing).</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly DispatcherTimer _pollTimer;
    private string? _loadedPath;
    private long _readPosition;

    public LogViewerWindow()
    {
        InitializeComponent();

        // FluentWindow's own OnSourceInitialized unconditionally forces WindowStyle back to
        // SingleBorderWindow and only hides the resulting native chrome via a WindowChrome hack
        // that's gated on DWM composition being enabled, which WinPE never has. Reasserting
        // WindowStyle = None alone (even though SourceInitialized fires after that base logic
        // runs) was verified NOT to reliably clear the native chrome in real WinPE testing, so
        // strip the caption/border style bits directly on the HWND and collapse the non-client
        // area outright. See NativeWindowChromeFix for the full explanation.
        SourceInitialized += (_, _) =>
        {
            WindowStyle = WindowStyle.None;
            NativeWindowChromeFix.RemoveNativeCaption(this);
        };

        // Live-tails the log file while this window is open, like CMTrace, so a technician
        // watching a long-running step doesn't need to keep clicking Refresh.
        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += (_, _) => PollForNewContent();
        Closed += (_, _) => _pollTimer.Stop();

        LoadLog();
        _pollTimer.Start();
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

    /// <summary>Full (re)load of the log file's tail — used on open, manual Refresh, and whenever
    /// the file is found to have rotated/shrunk since the last poll.</summary>
    private void LoadLog()
    {
        var path = LoggingConfiguration.CurrentLogFilePath;
        LogPathText.Text = path;
        _loadedPath = path;

        try
        {
            if (!File.Exists(path))
            {
                LogTextBox.Text = $"No log file found yet at:\n{path}";
                _readPosition = 0;
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
            _readPosition = stream.Length;
            ScrollToLatest();
        }
        catch (Exception ex)
        {
            LogTextBox.Text = $"Could not read log file:\n{path}\n\n{ex.Message}";
            _readPosition = 0;
        }
    }

    /// <summary>Appends only the bytes written since the last read (like CMTrace's tail-follow),
    /// instead of re-reading the whole file every tick.</summary>
    private void PollForNewContent()
    {
        var path = LoggingConfiguration.CurrentLogFilePath;

        // Day-rollover or the file not existing yet when the window was first opened — reload
        // from scratch so the viewer picks up the newly-active file.
        if (path != _loadedPath || !File.Exists(path))
        {
            LoadLog();
            return;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // File shrank (rolled over to a new sequence file, or was cleared) — the byte offset
            // we were tailing from no longer means anything, so reload the tail from scratch.
            if (stream.Length < _readPosition)
            {
                LoadLog();
                return;
            }

            if (stream.Length == _readPosition) return; // nothing new since the last poll

            stream.Seek(_readPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var appended = reader.ReadToEnd();
            _readPosition = stream.Length;

            if (appended.Length == 0) return;

            LogTextBox.AppendText(appended);
            ScrollToLatest();
        }
        catch
        {
            // Best-effort tailing — keep showing what's already loaded rather than replacing it
            // with an error; the next tick will retry.
        }
    }

    /// <summary>Always scrolls to the bottom on new content — this viewer is a live tail, not a
    /// document a technician needs to hold a scroll position in (CMTrace's default behavior).</summary>
    private void ScrollToLatest()
    {
        LogTextBox.CaretIndex = LogTextBox.Text.Length;
        LogTextBox.ScrollToEnd();
    }
}

