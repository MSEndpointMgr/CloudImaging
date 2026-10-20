using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using CloudImaging.Client.Services;

namespace CloudImaging.Client.Logging;

/// <summary>
/// Splits caught failures into expected network/service ones, logged as a single line with their
/// reason, and anything else, logged with its stack trace so a real defect stays diagnosable.
/// </summary>
internal static class LoggedFailure
{
    /// <summary>True for failures that are routine on a missing or flaky network, or a service that refused a request.</summary>
    public static bool IsExpected(Exception ex) => ex is DeviceGatewayApiException
        or HttpRequestException
        or TimeoutException
        or SocketException
        or IOException { InnerException: SocketException }
        // HttpClient reports its own timeout as a TaskCanceledException.
        or TaskCanceledException;

    /// <summary>One-line reason, with the inner cause appended when it adds something (e.g. "No such host is known.").</summary>
    public static string Reason(Exception ex)
    {
        var inner = ex.InnerException?.Message;
        return string.IsNullOrWhiteSpace(inner) || ex.Message.Contains(inner, StringComparison.Ordinal)
            ? ex.Message
            : $"{ex.Message} ({inner})";
    }

    /// <summary>The exception to attach to the log entry: null for expected failures, so no stack trace is written.</summary>
    public static Exception? StackTrace(Exception ex) => IsExpected(ex) ? null : ex;
}
