using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Wraps an <see cref="ILogger"/> so that every Information-level-and-above message is also
/// forwarded to a callback (<see cref="ViewModels.ProgressViewModel.AppendActivity"/> in
/// practice) in addition to going through to the inner logger exactly as before — nothing is
/// lost from the rolling file log. Debug/Trace messages (e.g. HTTP request/response chatter)
/// are not forwarded, since they aren't the kind of technician-facing "step" the ticker should
/// surface.
/// </summary>
internal sealed class ProgressActivityLogger(ILogger inner, Action<string> onActivity) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        inner.Log(logLevel, eventId, state, exception, formatter);

        if (logLevel >= LogLevel.Information)
        {
            onActivity(formatter(state, exception));
        }
    }
}

/// <summary>
/// An <see cref="ILoggerFactory"/> decorator used only for the pipeline's step services
/// (DiskFormatService, ImageDownloadService, ImageApplyService, BootConfigurationService,
/// RecoveryImageService) so that the real commands/sub-steps each one already logs — "Target
/// disk selected for formatting: disk 0.", "Starting DISM: {Args}", "Starting bcdboot
/// {Arguments}", "Starting reagentc.exe {Arguments}", etc. — also appear in ProgressView's
/// activity ticker (FR-007), instead of only the coarse one-line-per-phase StatusMessage text.
/// Does not own <paramref name="inner"/>; disposing this instance is a no-op.
/// </summary>
internal sealed class ProgressActivityLoggerFactory(ILoggerFactory inner, Action<string> onActivity) : ILoggerFactory
{
    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public ILogger CreateLogger(string categoryName) => new ProgressActivityLogger(inner.CreateLogger(categoryName), onActivity);

    public void Dispose()
    {
        // Intentionally does not dispose the wrapped factory — it is owned by the caller
        // (App.xaml.cs) and shared across the whole application's lifetime.
    }
}
