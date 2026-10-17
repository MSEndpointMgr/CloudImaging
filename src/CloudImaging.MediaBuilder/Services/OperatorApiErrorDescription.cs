using System.Net;
using System.Net.Http;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Turns Operator API failures into text an operator can act on. A 401/403 here is always an
/// app role assignment problem on the Operator API enterprise application, which the raw
/// "Response status code does not indicate success" message gives no hint of.
/// </summary>
public static class OperatorApiErrorDescription
{
    /// <summary>User-facing remediation message for a 401/403 response from the Operator API.</summary>
    public const string AccessDenied =
        "The Cloud Imaging Operator API denied this request. Your account needs the " +
        "CloudImaging.MediaBuilderAccess app role on the Cloud Imaging Operator API enterprise " +
        "application, which is granted separately from your Technician or Administrator role. " +
        "Ask your Cloud Imaging administrator to assign it.";

    /// <summary>True when the given exception (or any inner exception in its chain) represents a 401/403 response from the Operator API.</summary>
    public static bool IsAccessDenied(Exception ex)
    {
        // Operator API calls made deep inside generation surface wrapped, so check the chain.
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized })
                return true;
        }
        return false;
    }

    /// <summary>Returns a user-facing description of the given exception, substituting <see cref="AccessDenied"/> for 401/403 responses.</summary>
    public static string Describe(Exception ex) => IsAccessDenied(ex) ? AccessDenied : ex.Message;
}
