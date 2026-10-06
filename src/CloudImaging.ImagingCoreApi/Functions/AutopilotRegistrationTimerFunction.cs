using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Every two minutes: advances in-flight Autopilot imports from Intune's processing state and
/// expires undecided requests, purging their hardware hash.
/// </summary>
public sealed partial class AutopilotRegistrationTimerFunction
{
    private readonly AutopilotRegistrationService _service;
    private readonly ILogger<AutopilotRegistrationTimerFunction> _logger;

    public AutopilotRegistrationTimerFunction(AutopilotRegistrationService service, ILogger<AutopilotRegistrationTimerFunction> logger)
    {
        _service = service;
        _logger = logger;
    }

    [Function("AutopilotRegistrationTimer")]
    public async Task Run(
        [TimerTrigger("0 */2 * * * *")] TimerInfo timer,
        FunctionContext context)
    {
        var changed = await _service.ProcessAsync(context.CancellationToken);
        if (changed > 0)
        {
            LogTick(_logger, changed);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Autopilot registration timer advanced {Changed} request(s).")]
    private static partial void LogTick(ILogger logger, int changed);
}
