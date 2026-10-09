using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudImaging.Client.Services;

/// <summary>
/// Typed HTTP client for the Cloud Imaging Client → Device Gateway API calls (FR-001, FR-013).
/// All requests are authenticated by the mTLS boot-media certificate on the <see cref="HttpClientHandler"/>.
/// The device-session token Bearer is set on subsequent calls after session creation.
///
/// Every request/response is logged at Debug level to the local rolling log (FR-066: the log
/// must capture every request call, step, and payload). Values that could be used to impersonate
/// the device or session — the passcode, the device-session bearer token, proof-of-possession
/// signatures, and SAS query strings — are NEVER written to the log; see
/// <see cref="RedactSasUrl"/> and the redacted literals noted in each Log* message.
/// </summary>
public sealed partial class DeviceGatewayApiClient
{
    private readonly HttpClient _http;
    private readonly X509Certificate2? _signingCertificate;
    private readonly ILogger<DeviceGatewayApiClient> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <param name="http">The mTLS-configured HTTP client.</param>
    /// <param name="signingCertificate">
    /// The boot-media certificate (with private key) used to sign the session-bootstrap
    /// proof-of-possession (FR-069). When null, no proof is attached (the Device Gateway will
    /// reject the request — this is intended only for tests/tools that provide their own payload).
    /// </param>
    /// <param name="logger">
    /// Optional so existing tests/tools can keep constructing this client without a logging
    /// pipeline wired up; defaults to a no-op logger.
    /// </param>
    public DeviceGatewayApiClient(
        HttpClient http,
        X509Certificate2? signingCertificate = null,
        ILogger<DeviceGatewayApiClient>? logger = null)
    {
        _http = http;
        _signingCertificate = signingCertificate;
        _logger = logger ?? NullLogger<DeviceGatewayApiClient>.Instance;
    }

    /// <summary>
    /// POST /api/v1/sessions — Register a new imaging session.
    /// Returns (sessionId, deviceSessionToken, passcode, state).
    /// </summary>
    public async Task<CreateSessionResponse?> CreateSessionAsync(
        DeviceRegistrationPayload payload,
        CancellationToken ct = default)
    {
        var signedPayload = SignPayload(payload);
        LogCreateSessionRequest(_logger, payload.SerialNumber, payload.Manufacturer, payload.Model);

        var response = await _http.PostAsJsonAsync("/api/v1/sessions", signedPayload, JsonOptions, ct);
        LogHttpResponse(_logger, "POST", "/api/v1/sessions", (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            // Carries the real rejection reason (e.g. an mTLS/proof-of-possession detail from
            // MtlsCertificateValidationMiddleware or CreateSessionFunction) into both the thrown
            // exception's Message (shown on-screen by OperationSelectionViewModel) and the local
            // log, instead of a generic "401 (Unauthorized)" that gives no clue why.
            var failure = await DeviceGatewayApiException.FromResponseAsync(response, ct);
            LogHttpRequestFailed(_logger, "POST", "/api/v1/sessions", (int)response.StatusCode, failure.ProblemType, failure.Message);
            throw failure;
        }

        var result = await response.Content.ReadFromJsonAsync<CreateSessionResponse>(JsonOptions, ct);
        if (result is not null)
            LogCreateSessionResponse(_logger, result.SessionId, result.State);
        return result;
    }

    /// <summary>
    /// Attaches an application-layer proof-of-possession to the registration payload by signing a
    /// fresh challenge (serial + UTC timestamp + nonce) with the boot-media private key (FR-069).
    /// </summary>
    private DeviceRegistrationPayload SignPayload(DeviceRegistrationPayload payload)
    {
        if (_signingCertificate is null)
            return payload;

        using var rsa = _signingCertificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Boot-media certificate has no RSA private key.");

        var timestampUtc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var challenge = DevicePayloadSignature.BuildChallenge(payload.SerialNumber, timestampUtc, nonce);
        var signature = rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return new DeviceRegistrationPayload
        {
            SerialNumber = payload.SerialNumber,
            Manufacturer = payload.Manufacturer,
            Model        = payload.Model,
            MacAddress   = payload.MacAddress,
            Hardware     = payload.Hardware,
            LocationId   = payload.LocationId,
            LocationName = payload.LocationName,
            Architecture = payload.Architecture,
            SecurityPosture = payload.SecurityPosture,
            ProofOfPossession = new DeviceProofOfPossession
            {
                Nonce        = nonce,
                TimestampUtc = timestampUtc,
                Signature    = Convert.ToBase64String(signature),
            },
        };
    }

    /// <summary>
    /// GET /api/v1/sessions/{sessionId}/status — Poll the current session state.
    /// Requires the device-session token Bearer to be set in <see cref="HttpClient.DefaultRequestHeaders"/>.
    /// On a non-success response, throws <see cref="DeviceGatewayApiException"/> (rather than a generic
    /// <see cref="HttpRequestException"/>) carrying the RFC7807 problem "type"/"detail" from the response
    /// body, if present, so callers can distinguish an mTLS certificate rejection from a rejected/expired/
    /// revoked device-session Bearer token — both surface as HTTP 401 but need different user-facing text.
    /// </summary>
    public async Task<SessionStatusResponse?> GetSessionStatusAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        var path = $"/api/v1/sessions/{sessionId}/status";
        LogHttpRequest(_logger, "GET", path);
        var response = await _http.GetAsync(path, ct);
        LogHttpResponse(_logger, "GET", path, (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            var exception = await DeviceGatewayApiException.FromResponseAsync(response, ct);
            LogHttpRequestFailed(_logger, "GET", path, (int)response.StatusCode, exception.ProblemType, exception.Message);
            throw exception;
        }

        var result = await response.Content.ReadFromJsonAsync<SessionStatusResponse>(JsonOptions, ct);
        if (result is not null)
            LogSessionStatusResponse(_logger, sessionId, result.State, result.CurrentStep, result.OverallProgressPercent);
        return result;
    }

    /// <summary>
    /// Sets the device-session Bearer token for all subsequent requests.
    /// Called immediately after <see cref="CreateSessionAsync"/> succeeds.
    /// </summary>
    public void SetSessionToken(string token)
    {
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        LogSessionTokenSet(_logger);
    }

    /// <summary>POST /api/v1/sessions/{sessionId}/progress — Report imaging step progress.</summary>
    public async Task ReportProgressAsync(
        Guid sessionId,
        object payload,
        CancellationToken ct = default)
    {
        var path = $"/api/v1/sessions/{sessionId}/progress";
        LogHttpRequest(_logger, "POST", path);
        var response = await _http.PostAsJsonAsync(path, payload, JsonOptions, ct);
        LogHttpResponse(_logger, "POST", path, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await DeviceGatewayApiException.FromResponseAsync(response, ct);
            LogHttpRequestFailed(_logger, "POST", path, (int)response.StatusCode, failure.ProblemType, failure.Message);
            throw failure;
        }
    }

    /// <summary>
    /// POST /api/v1/sessions/{sessionId}/sas/refresh — Refresh the SAS token URL.
    /// Returns both the (possibly unchanged) URL and its actual server-computed expiry, so the caller
    /// never has to guess the new expiry (see <see cref="SasRefreshCoordinator"/>).
    /// </summary>
    public async Task<SasRefreshResult> RefreshSasTokenAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        var path = $"/api/v1/sessions/{sessionId}/sas/refresh";
        LogHttpRequest(_logger, "POST", path);
        var response = await _http.PostAsync(path, null, ct);
        LogHttpResponse(_logger, "POST", path, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await DeviceGatewayApiException.FromResponseAsync(response, ct);
            LogHttpRequestFailed(_logger, "POST", path, (int)response.StatusCode, failure.ProblemType, failure.Message);
            throw failure;
        }
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var url = doc.RootElement.TryGetProperty("sasTokenUrl", out var p) ? p.GetString() : null;
        var expiresAt = doc.RootElement.TryGetProperty("sasTokenUrlExpiresAt", out var e)
            && DateTimeOffset.TryParse(
                e.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
        var redactedSasUrl = RedactSasUrl(url);
        LogSasRefreshed(_logger, sessionId, redactedSasUrl, expiresAt);
        return new SasRefreshResult(url, expiresAt);
    }

    /// <summary>
    /// GET /api/v1/boot-image/latest — Retrieve the latest published boot image's version,
    /// hash, and SAS download URL, used by <see cref="BootImageSelfUpdateService"/> to detect
    /// stale boot media (T071b, FR-059a). Requires only the mTLS boot-media certificate — no
    /// device-session token — so it can run independently of session bootstrap. Returns null on
    /// any non-success response (no active session/token is required, but network/service
    /// errors are treated as "nothing to update" rather than thrown, since this check must never
    /// block Client startup). Also null on 204, which means this stick runs a pre-production
    /// image that must not be replaced.
    /// </summary>
    public async Task<LatestBootImageInfo?> GetLatestBootImageAsync(MachineArchitecture architecture = MachineArchitecture.X64, Guid? currentBootImageId = null, CancellationToken ct = default)
    {
        var path = $"/api/v1/boot-image/latest?architecture={MachineArchitecturePlatform.Slug(architecture)}"
            + (currentBootImageId is { } current ? $"&currentBootImageId={current}" : string.Empty);
        LogHttpRequest(_logger, "GET", path);
        var response = await _http.GetAsync(path, ct);
        LogHttpResponse(_logger, "GET", path, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await DeviceGatewayApiException.FromResponseAsync(response, ct);
            LogHttpRequestFailed(_logger, "GET", path, (int)response.StatusCode, failure.ProblemType, failure.Message);
            return null;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<LatestBootImageInfo>(JsonOptions, ct);
        if (result is not null)
        {
            var redactedSasUrl = RedactSasUrl(result.SasTokenUrl);
            LogLatestBootImage(_logger, result.Version, result.Sha256Hash, redactedSasUrl);
        }
        return result;
    }

    /// <summary>
    /// GET /api/v1/recovery-image/latest — Retrieve the latest published recovery (WinRE)
    /// image's version, hash, and SAS download URL, used by <see cref="Services.RecoveryImageService"/>
    /// to apply the recovery image to the Recovery partition. Requires the device-session token
    /// Bearer (set via <see cref="SetSessionToken"/>). Returns null on any non-success response.
    /// </summary>
    public async Task<LatestRecoveryImageInfo?> GetLatestRecoveryImageAsync(MachineArchitecture architecture = MachineArchitecture.X64, CancellationToken ct = default)
    {
        var path = $"/api/v1/recovery-image/latest?architecture={MachineArchitecturePlatform.Slug(architecture)}";
        LogHttpRequest(_logger, "GET", path);
        var response = await _http.GetAsync(path, ct);
        LogHttpResponse(_logger, "GET", path, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await DeviceGatewayApiException.FromResponseAsync(response, ct);
            LogHttpRequestFailed(_logger, "GET", path, (int)response.StatusCode, failure.ProblemType, failure.Message);
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<LatestRecoveryImageInfo>(JsonOptions, ct);
        if (result is not null)
        {
            var redactedSasUrl = RedactSasUrl(result.SasTokenUrl);
            LogLatestRecoveryImage(_logger, result.Version, result.Sha256Hash, redactedSasUrl);
        }
        return result;
    }

    /// <summary>
    /// POST /api/v1/sessions/{sessionId}/logs/upload-url — Requests a short-lived write SAS URL
    /// the Client can PUT its current local diagnostic log to, used by
    /// <see cref="Services.LogUploadService"/> on any terminal imaging failure. Requires the
    /// device-session token Bearer. Returns null on any non-success response — this must never
    /// block or mask the real failure being reported.
    /// </summary>
    public async Task<LogUploadUrlResponse?> RequestLogUploadUrlAsync(Guid sessionId, CancellationToken ct = default)
    {
        var path = $"/api/v1/sessions/{sessionId}/logs/upload-url";
        LogHttpRequest(_logger, "POST", path);
        var response = await _http.PostAsync(path, null, ct);
        LogHttpResponse(_logger, "POST", path, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await DeviceGatewayApiException.FromResponseAsync(response, ct);
            LogHttpRequestFailed(_logger, "POST", path, (int)response.StatusCode, failure.ProblemType, failure.Message);
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<LogUploadUrlResponse>(JsonOptions, ct);
        if (result is not null)
            LogLogUploadUrlIssued(_logger, sessionId, result.FileName);
        return result;
    }

    // ── Autopilot hardware hash registration (independent of imaging sessions) ────────────────────

    /// <summary>
    /// GET /api/v1/autopilot/availability: whether the portal allows Autopilot registration.
    /// mTLS only. Returns null when the answer could not be obtained (offline, older backend).
    /// </summary>
    public async Task<bool?> GetAutopilotAvailabilityAsync(CancellationToken ct = default)
    {
        const string path = "/api/v1/autopilot/availability";
        LogHttpRequest(_logger, "GET", path);
        var response = await _http.GetAsync(path, ct);
        LogHttpResponse(_logger, "GET", path, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await DeviceGatewayApiException.FromResponseAsync(response, ct);
            LogHttpRequestFailed(_logger, "GET", path, (int)response.StatusCode, failure.ProblemType, failure.Message);
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<AutopilotAvailability>(JsonOptions, ct);
        return result?.Enabled;
    }

    /// <summary>
    /// POST /api/v1/autopilot/registrations: submits the hardware hash for approval, signed with
    /// the boot-media private key over a challenge that includes the hash itself.
    /// </summary>
    public async Task<AutopilotSubmissionResponse> SubmitAutopilotHashAsync(AutopilotHashSubmission submission, CancellationToken ct = default)
    {
        const string path = "/api/v1/autopilot/registrations";
        LogAutopilotSubmitRequest(_logger, submission.SerialNumber, submission.Manufacturer, submission.Model);
        var response = await _http.PostAsJsonAsync(path, SignAutopilotSubmission(submission), JsonOptions, ct);
        LogHttpResponse(_logger, "POST", path, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await DeviceGatewayApiException.FromResponseAsync(response, ct);
            LogHttpRequestFailed(_logger, "POST", path, (int)response.StatusCode, failure.ProblemType, failure.Message);
            throw failure;
        }

        var result = await response.Content.ReadFromJsonAsync<AutopilotSubmissionResponse>(JsonOptions, ct)
            ?? throw new InvalidOperationException("The Device Gateway returned an empty Autopilot submission response.");
        LogAutopilotSubmitResponse(_logger, result.RequestId, result.ReferenceCode, result.State);
        return result;
    }

    /// <summary>
    /// GET /api/v1/autopilot/registrations/{id}: reads the request's status with its own status
    /// token, sent per request so it never replaces a device-session token set on the client.
    /// </summary>
    public async Task<AutopilotRegistrationStatus> GetAutopilotStatusAsync(Guid requestId, string statusToken, CancellationToken ct = default)
    {
        var path = $"/api/v1/autopilot/registrations/{requestId}";
        LogHttpRequest(_logger, "GET", path);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", statusToken);
        var response = await _http.SendAsync(request, ct);
        LogHttpResponse(_logger, "GET", path, (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await DeviceGatewayApiException.FromResponseAsync(response, ct);
            LogHttpRequestFailed(_logger, "GET", path, (int)response.StatusCode, failure.ProblemType, failure.Message);
            throw failure;
        }

        return await response.Content.ReadFromJsonAsync<AutopilotRegistrationStatus>(JsonOptions, ct)
            ?? throw new InvalidOperationException("The Device Gateway returned an empty Autopilot status response.");
    }

    private AutopilotHashSubmission SignAutopilotSubmission(AutopilotHashSubmission submission)
    {
        if (_signingCertificate is null)
            return submission;

        using var rsa = _signingCertificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Boot-media certificate has no RSA private key.");

        var timestampUtc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var challenge = DevicePayloadSignature.BuildAutopilotChallenge(submission.SerialNumber, submission.HardwareHash, timestampUtc, nonce);
        var signature = rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return new AutopilotHashSubmission
        {
            SerialNumber = submission.SerialNumber,
            Manufacturer = submission.Manufacturer,
            Model = submission.Model,
            HardwareHash = submission.HardwareHash,
            Architecture = submission.Architecture,
            LocationId = submission.LocationId,
            LocationName = submission.LocationName,
            ClientVersion = submission.ClientVersion,
            ProofOfPossession = new DeviceProofOfPossession
            {
                Nonce = nonce,
                TimestampUtc = timestampUtc,
                Signature = Convert.ToBase64String(signature),
            },
        };
    }

    // ── Logging (FR-066: every request/step/payload — secrets are always redacted) ──────────────────

    /// <summary>
    /// Strips the query string (which carries the SAS signature) from a SAS URL before it is
    /// logged, leaving only the scheme/host/path so the log still shows which blob was involved
    /// without leaking a credential that grants direct access to it.
    /// </summary>
    private static string RedactSasUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "(none)";
        var queryIndex = url.IndexOf('?');
        return queryIndex >= 0 ? url[..queryIndex] + "?<redacted>" : url;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "HTTP {Method} {Path} \u2192")]
    private static partial void LogHttpRequest(ILogger logger, string method, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "HTTP {Method} {Path} \u2190 {StatusCode}")]
    private static partial void LogHttpResponse(ILogger logger, string method, string path, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HTTP {Method} {Path} \u2190 {StatusCode} (problem type: {ProblemType}; detail: {Detail}).")]
    private static partial void LogHttpRequestFailed(ILogger logger, string method, string path, int statusCode, string? problemType, string? detail);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "CreateSession request: serial={SerialNumber} manufacturer={Manufacturer} model={Model} (proof-of-possession signature omitted).")]
    private static partial void LogCreateSessionRequest(ILogger logger, string serialNumber, string manufacturer, string model);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "CreateSession response: sessionId={SessionId} state={State} (passcode/device-session token omitted).")]
    private static partial void LogCreateSessionResponse(ILogger logger, Guid sessionId, string state);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Device-session bearer token set for subsequent requests (value omitted).")]
    private static partial void LogSessionTokenSet(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "SessionStatus response: sessionId={SessionId} state={State} currentStep={CurrentStep} progress={ProgressPercent}%.")]
    private static partial void LogSessionStatusResponse(ILogger logger, Guid sessionId, string? state, string? currentStep, int progressPercent);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "SAS token refreshed for session {SessionId}: url={SasUrl} expiresAt={ExpiresAt} (signature omitted).")]
    private static partial void LogSasRefreshed(ILogger logger, Guid sessionId, string sasUrl, DateTimeOffset? expiresAt);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Latest boot image: version={Version} sha256={Sha256Hash} url={SasUrl} (signature omitted).")]
    private static partial void LogLatestBootImage(ILogger logger, string version, string sha256Hash, string sasUrl);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Latest recovery image: version={Version} sha256={Sha256Hash} url={SasUrl} (signature omitted).")]
    private static partial void LogLatestRecoveryImage(ILogger logger, string version, string sha256Hash, string sasUrl);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Log upload URL issued for session {SessionId}: fileName={FileName} (upload URL signature omitted).")]
    private static partial void LogLogUploadUrlIssued(ILogger logger, Guid sessionId, string fileName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Autopilot submission: serial={SerialNumber} manufacturer={Manufacturer} model={Model} (hardware hash and signature omitted).")]
    private static partial void LogAutopilotSubmitRequest(ILogger logger, string serialNumber, string manufacturer, string model);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Autopilot submission accepted: requestId={RequestId} reference={ReferenceCode} state={State} (status token omitted).")]
    private static partial void LogAutopilotSubmitResponse(ILogger logger, Guid requestId, string referenceCode, CloudImaging.Contracts.Enums.AutopilotRegistrationState state);
}

/// <summary>
/// Response from the Device Gateway API POST /api/v1/sessions endpoint.
/// </summary>
public sealed class CreateSessionResponse
{
    /// <summary>Identifier for the created session.</summary>
    public Guid SessionId { get; init; }

    /// <summary>Bearer token the device uses to authenticate subsequent session calls.</summary>
    public string DeviceSessionToken { get; init; } = string.Empty;

    /// <summary>The coupling passcode, returned only at creation.</summary>
    public string Passcode { get; init; } = string.Empty;

    /// <summary>Lifecycle state the session was created in.</summary>
    public string State { get; init; } = string.Empty;
}

/// <summary>
/// Response from the Device Gateway API GET /api/v1/sessions/{sessionId}/status endpoint (T043).
/// Always includes <see cref="CurrentStep"/> and <see cref="OverallProgressPercent"/> (plan.md constraint).
/// </summary>
public sealed class SessionStatusResponse
{
    /// <summary>Identifier of the session this status is for.</summary>
    public Guid SessionId { get; init; }

    /// <summary>Current lifecycle state of the session.</summary>
    public string? State { get; init; }

    /// <summary>Name of the imaging step currently in progress, or null when none has started.</summary>
    public string? CurrentStep { get; init; }

    /// <summary>Overall imaging progress, 0-100.</summary>
    public int OverallProgressPercent { get; init; }

    /// <summary>SAS URL to download the assigned OS image.</summary>
    public string? SasTokenUrl { get; init; }

    /// <summary>When <see cref="SasTokenUrl"/> expires.</summary>
    public string? SasTokenUrlExpiresAt { get; init; }

    /// <summary>SHA-256 hash of the assigned OS image, for cache validation.</summary>
    public string? Sha256Hash { get; init; }

    /// <summary>Architecture of the assigned OS image; null from backends that predate it (x64).</summary>
    public MachineArchitecture? OsImageArchitecture { get; init; }

    /// <summary>
    /// The partitioning scheme snapshotted onto this session at creation time. Null only if the
    /// backend has not been upgraded yet (defensive — the server always populates this today).
    /// </summary>
    public PartitioningScheme? PartitioningScheme { get; init; }

    /// <summary>Pre-flight checks evaluated when the session was created; null from backends that predate them.</summary>
    public IReadOnlyList<PreFlightCheckResult>? PreFlightChecks { get; init; }
}

/// <summary>Result of a SAS token refresh call — see <see cref="DeviceGatewayApiClient.RefreshSasTokenAsync"/>.</summary>
public sealed record SasRefreshResult(string? SasTokenUrl, DateTimeOffset? ExpiresAt);

/// <summary>
/// Response from the Device Gateway API POST /api/v1/sessions/{sessionId}/logs/upload-url
/// endpoint — see <see cref="DeviceGatewayApiClient.RequestLogUploadUrlAsync"/>.
/// </summary>
public sealed class LogUploadUrlResponse
{
    /// <summary>Name to give the uploaded log file.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Time-limited SAS URL to upload the log file to.</summary>
    public string UploadUrl { get; init; } = string.Empty;

    /// <summary>When <see cref="UploadUrl"/> expires.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Thrown when the Device Gateway API returns a non-success response. Carries the RFC7807
/// "type"/"detail" fields from the response body (when present) so callers can distinguish, e.g.,
/// an mTLS certificate rejection (<see cref="CloudImaging.Client.Services"/> boot-media cert) from a
/// rejected/expired/revoked device-session Bearer token — both surface as HTTP 401.
/// </summary>
public sealed class DeviceGatewayApiException : Exception
{
    /// <summary>Well-known problem type used by <c>DeviceSessionTokenValidationMiddleware</c> for token failures.</summary>
    public const string TokenProblemType = "https://cloudimaging.io/errors/unauthorized";

    private const int MaxPlainTextDetailLength = 300;

    /// <summary>HTTP status code the Device Gateway API returned.</summary>
    public System.Net.HttpStatusCode StatusCode { get; }

    /// <summary>RFC7807 "type" field from the response body, if present.</summary>
    public string? ProblemType { get; }

    /// <summary>Builds the exception from an already-known status code, problem type and detail.</summary>
    public DeviceGatewayApiException(System.Net.HttpStatusCode statusCode, string? problemType, string? detail)
        : base(detail ?? $"Device Gateway API returned HTTP {(int)statusCode}.")
    {
        StatusCode = statusCode;
        ProblemType = problemType;
    }

    internal static async Task<DeviceGatewayApiException> FromResponseAsync(
        System.Net.Http.HttpResponseMessage response, CancellationToken ct)
    {
        string? problemType = null;
        string? detail = null;
        string body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            // Body unreadable (e.g. connection dropped mid-response) — leave it empty.
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                problemType = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
            }
            catch (System.Text.Json.JsonException)
            {
                // Several Device Gateway endpoints (e.g. CreateSession's mTLS/proof-of-possession
                // rejections) return a short plain-text reason instead of RFC7807 JSON — fall back
                // to the raw body so that reason still reaches the log/UI instead of being silently
                // dropped.
            }

            // Only a short one-line reason is usable; an App Service HTML error page would flood the screen.
            var trimmed = body.Trim();
            var isHtml = string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith('<');
            if (detail is null && !isHtml && trimmed.Length <= MaxPlainTextDetailLength && !trimmed.Contains('\n'))
                detail = trimmed;
        }

        return new DeviceGatewayApiException(response.StatusCode, problemType, detail);
    }
}
