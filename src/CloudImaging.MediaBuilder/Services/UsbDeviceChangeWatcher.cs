using System.Management;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Watches for USB storage device plug/unplug events via WMI (T071, FR-054) so
/// <see cref="ViewModels.PrepareStorageDeviceViewModel"/> can auto-refresh its disk list
/// without the technician needing to click Refresh manually. This is a plain WPF desktop app
/// (not UWP), so the WinRT <c>Windows.Devices.Enumeration.DeviceWatcher</c> API isn't available;
/// <see cref="ManagementEventWatcher"/> is used instead, consistent with the WMI-based disk
/// enumeration already used by <see cref="UsbSafetyValidationService"/>.
///
/// A single USB device plug/unplug typically raises several <c>Win32_VolumeChangeEvent</c>
/// notifications in quick succession (one per volume/partition on the device), so events are
/// debounced into a single <see cref="DeviceChanged"/> notification.
/// </summary>
public sealed partial class UsbDeviceChangeWatcher : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(750);

    private readonly ILogger<UsbDeviceChangeWatcher> _logger;
    private readonly object _gate = new();
    private ManagementEventWatcher? _watcher;
    private Timer? _debounceTimer;
    private bool _disposed;

    public UsbDeviceChangeWatcher(ILogger<UsbDeviceChangeWatcher> logger) => _logger = logger;

    /// <summary>
    /// Raised (on a thread-pool timer thread — subscribers must marshal to the UI thread
    /// themselves) after a debounced burst of WMI volume-change events.
    /// </summary>
    public event EventHandler? DeviceChanged;

    /// <summary>Starts watching for device-change events. Safe to call more than once (no-op after the first).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_watcher is not null || _disposed)
                return;

            try
            {
                // EventType 2 = device arrival/configuration change, 3 = device removal.
                var query = new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2 OR EventType = 3");
                _watcher = new ManagementEventWatcher(query);
                _watcher.EventArrived += OnEventArrived;
                _watcher.Start();
                LogStarted(_logger);
            }
            catch (Exception ex)
            {
                LogStartFailed(_logger, ex);
                _watcher?.Dispose();
                _watcher = null;
            }
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        // Debounce: restart a one-shot timer on every event; only the last event in a burst
        // actually raises DeviceChanged, ~750ms after the burst quiets down.
        lock (_gate)
        {
            if (_disposed)
                return;

            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(
                _ => DeviceChanged?.Invoke(this, EventArgs.Empty),
                null,
                DebounceDelay,
                Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_watcher is not null)
            {
                _watcher.EventArrived -= OnEventArrived;
                try { _watcher.Stop(); } catch { /* best-effort */ }
                _watcher.Dispose();
                _watcher = null;
            }

            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "USB device-change watcher started.")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "USB device-change watcher failed to start; falling back to manual Refresh only.")]
    private static partial void LogStartFailed(ILogger logger, Exception ex);
}
