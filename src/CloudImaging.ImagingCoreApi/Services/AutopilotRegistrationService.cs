using System.Security.Cryptography;
using System.Text;
using Azure;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services;

public enum AutopilotOutcome
{
    Ok,
    NotFound,
    Conflict,
    Invalid,
    Disabled,
    GraphFailed,
}

public sealed record AutopilotResult<T>(AutopilotOutcome Outcome, T? Value = default, string? Error = null);

public static class AutopilotResult
{
    public static AutopilotResult<T> Ok<T>(T value) => new(AutopilotOutcome.Ok, value);
    public static AutopilotResult<T> Fail<T>(AutopilotOutcome outcome, string error) => new(outcome, default, error);
}

/// <summary>
/// Owns the Autopilot registration lifecycle: device submission, approver decisions, the Graph
/// import and its asynchronous completion. Every state change goes through an ETag-guarded replace
/// so two approvers, or an approver and the timer, can never act on the same request twice.
/// </summary>
public sealed partial class AutopilotRegistrationService
{
    internal static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(60);
    internal static readonly TimeSpan ImportStartTimeout = TimeSpan.FromMinutes(10);
    internal const int MaxHashLength = 16_384;
    internal const int MaxReasonLength = 500;
    private const string ReferenceAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly AutopilotRegistrationRepository _requests;
    private readonly AutopilotGroupTagRepository _groupTags;
    private readonly LocationRepository _locations;
    private readonly PortalConfigurationRepository _config;
    private readonly AutopilotGraphClient _graph;
    private readonly TimeProvider _time;
    private readonly ILogger<AutopilotRegistrationService> _logger;

    public AutopilotRegistrationService(
        AutopilotRegistrationRepository requests,
        AutopilotGroupTagRepository groupTags,
        LocationRepository locations,
        PortalConfigurationRepository config,
        AutopilotGraphClient graph,
        ILogger<AutopilotRegistrationService> logger,
        TimeProvider? time = null)
    {
        _requests = requests;
        _groupTags = groupTags;
        _locations = locations;
        _config = config;
        _graph = graph;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<bool> IsEnabledAsync(CancellationToken ct) =>
        (await _config.GetAsync(ct)).AutopilotRegistrationEnabled;

    // ── Device submission ────────────────────────────────────────────────────

    public async Task<AutopilotResult<AutopilotSubmissionResponse>> SubmitAsync(AutopilotHashSubmission submission, CancellationToken ct)
    {
        var config = await _config.GetAsync(ct);
        if (!config.AutopilotRegistrationEnabled)
        {
            return AutopilotResult.Fail<AutopilotSubmissionResponse>(AutopilotOutcome.Disabled, "Autopilot registration is turned off in the portal.");
        }

        var validationError = ValidateSubmission(submission);
        if (validationError is not null)
        {
            return AutopilotResult.Fail<AutopilotSubmissionResponse>(AutopilotOutcome.Invalid, validationError);
        }

        var now = _time.GetUtcNow();
        var statusToken = NewStatusToken();
        var statusTokenHash = HashStatusToken(statusToken);
        var serial = submission.SerialNumber.Trim();
        var expiresAt = now.AddDays(config.AutopilotPendingExpiryDays);

        // Read from the hash itself, not trusted from the device, so the approver sees what Intune will get.
        var inspection = AutopilotHardwareHash.Inspect(submission.HardwareHash);
        var tpmVersion = inspection.Decoded ? inspection.TpmVersion : null;
        bool? preProvisioningReady = inspection.Decoded ? inspection.PreProvisioningReady : null;

        // A device resubmitting while its request is still open refreshes that request instead of queueing a duplicate.
        (AutopilotRegistrationRequest Request, ETag ETag)? open = null;
        await foreach (var candidate in _requests.ListBySerialAsync(serial, ct))
        {
            if (!AutopilotRegistrationRequest.IsTerminal(candidate.Request.State)
                && string.Equals(candidate.Request.Manufacturer, submission.Manufacturer.Trim(), StringComparison.OrdinalIgnoreCase)
                && (open is null || candidate.Request.SubmittedAt > open.Value.Request.SubmittedAt))
            {
                open = candidate;
            }
        }

        if (open is { } existing)
        {
            var refreshed = existing.Request.State == AutopilotRegistrationState.Importing
                ? existing.Request with { StatusTokenHash = statusTokenHash, UpdatedAt = now }
                : existing.Request with
                {
                    State = AutopilotRegistrationState.PendingApproval,
                    HardwareHash = submission.HardwareHash.Trim(),
                    StatusTokenHash = statusTokenHash,
                    Model = submission.Model.Trim(),
                    Architecture = submission.Architecture ?? MachineArchitecture.X64,
                    LocationId = submission.LocationId,
                    LocationName = submission.LocationName,
                    ClientVersion = submission.ClientVersion,
                    TpmVersion = tpmVersion,
                    PreProvisioningReady = preProvisioningReady,
                    SubmittedAt = now,
                    UpdatedAt = now,
                    ExpiresAt = expiresAt,
                    GroupTag = null,
                    GroupTagDefinitionId = null,
                    DecidedByUpn = null,
                    DecidedByObjectId = null,
                    DecidedAt = null,
                    ImportedIdentityId = null,
                    ImportStartedAt = null,
                    ImportErrorCode = null,
                    ImportErrorName = null,
                };

            if (await _requests.TryReplaceAsync(refreshed, existing.ETag, ct) is null)
            {
                return AutopilotResult.Fail<AutopilotSubmissionResponse>(AutopilotOutcome.Conflict, "The request changed while it was being resubmitted. Try again.");
            }

            LogResubmitted(_logger, refreshed.RequestId, refreshed.ReferenceCode);
            return AutopilotResult.Ok(ToSubmissionResponse(refreshed, statusToken));
        }

        var alreadyRegistered = false;
        try
        {
            alreadyRegistered = await _graph.IsSerialRegisteredAsync(serial, ct);
        }
        catch (Exception ex) when (IsGraphFailure(ex))
        {
            // The approver can still see a failed import, so a lookup outage must not block the submission.
            LogRegisteredLookupFailed(_logger, ex, serial);
        }

        var request = new AutopilotRegistrationRequest
        {
            RequestId = Guid.NewGuid(),
            ReferenceCode = NewReferenceCode(),
            SerialNumber = serial,
            Manufacturer = submission.Manufacturer.Trim(),
            Model = submission.Model.Trim(),
            Architecture = submission.Architecture ?? MachineArchitecture.X64,
            LocationId = submission.LocationId,
            LocationName = submission.LocationName,
            HardwareHash = alreadyRegistered ? null : submission.HardwareHash.Trim(),
            StatusTokenHash = statusTokenHash,
            State = alreadyRegistered ? AutopilotRegistrationState.AlreadyRegistered : AutopilotRegistrationState.PendingApproval,
            SubmittedAt = now,
            UpdatedAt = now,
            ExpiresAt = expiresAt,
            ClientVersion = submission.ClientVersion,
            TpmVersion = tpmVersion,
            PreProvisioningReady = preProvisioningReady,
        };

        await _requests.AddAsync(request, ct);
        LogSubmitted(_logger, request.RequestId, request.ReferenceCode, request.State);
        return AutopilotResult.Ok(ToSubmissionResponse(request, statusToken));
    }

    public async Task<AutopilotRegistrationStatus?> GetDeviceStatusAsync(Guid requestId, string statusToken, CancellationToken ct)
    {
        var found = await _requests.GetAsync(requestId, ct);
        if (found is not { } entry || !StatusTokenMatches(entry.Request.StatusTokenHash, statusToken))
        {
            return null;
        }

        var r = entry.Request;
        return new AutopilotRegistrationStatus
        {
            RequestId = r.RequestId,
            ReferenceCode = r.ReferenceCode,
            State = r.State,
            GroupTag = r.GroupTag,
            RejectionReason = r.RejectionReason,
            ImportErrorName = r.ImportErrorName,
            UpdatedAt = r.UpdatedAt,
        };
    }

    // ── Portal queries ───────────────────────────────────────────────────────

    /// <summary>The approval queue: every request that still needs an approver or is still importing, oldest first.</summary>
    public async Task<List<AutopilotRegistrationRequest>> ListOpenAsync(CancellationToken ct)
    {
        var results = new List<AutopilotRegistrationRequest>();
        await foreach (var (request, _) in _requests.ListAsync(ct))
        {
            if (!AutopilotRegistrationRequest.IsTerminal(request.State))
            {
                results.Add(request);
            }
        }

        results.Sort((a, b) => a.SubmittedAt.CompareTo(b.SubmittedAt));
        return results;
    }

    /// <summary>Handled requests closed within [<paramref name="from"/>, <paramref name="to"/>], newest first, for the audit report.</summary>
    public async Task<List<AutopilotRegistrationRequest>> ListHandledAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var results = new List<AutopilotRegistrationRequest>();
        await foreach (var (request, _) in _requests.ListAsync(ct))
        {
            // A handled request is never modified again, so UpdatedAt is when it was closed.
            if (AutopilotRegistrationRequest.IsTerminal(request.State) && request.UpdatedAt >= from && request.UpdatedAt <= to)
            {
                results.Add(request);
            }
        }

        results.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return results;
    }

    public async Task<AutopilotRegistrationDetail?> GetDetailAsync(Guid requestId, CancellationToken ct)
    {
        var found = await _requests.GetAsync(requestId, ct);
        if (found is not { } entry)
        {
            return null;
        }

        var config = await _config.GetAsync(ct);
        var (locationName, region, country) = await ResolveLocationAsync(entry.Request, ct);
        var definitions = await _groupTags.ListAsync(ct);

        return new AutopilotRegistrationDetail
        {
            Request = entry.Request,
            GroupTagRequired = config.AutopilotGroupTagRequired,
            LocationRegion = region,
            LocationCountryCode = country,
            GroupTagOptions = definitions.Select(d => ToOption(d, locationName, region, country)).ToList(),
        };
    }

    // ── Approver decisions ───────────────────────────────────────────────────

    public async Task<AutopilotResult<AutopilotRegistrationRequest>> ApproveAsync(Guid requestId, AutopilotDecision decision, CancellationToken ct)
    {
        var found = await _requests.GetAsync(requestId, ct);
        if (found is not { } entry)
        {
            return AutopilotResult.Fail<AutopilotRegistrationRequest>(AutopilotOutcome.NotFound, "Request not found.");
        }

        if (entry.Request.State != AutopilotRegistrationState.PendingApproval)
        {
            return AutopilotResult.Fail<AutopilotRegistrationRequest>(AutopilotOutcome.Conflict, $"The request is {entry.Request.State} and can no longer be approved.");
        }

        if (string.IsNullOrWhiteSpace(entry.Request.HardwareHash))
        {
            return AutopilotResult.Fail<AutopilotRegistrationRequest>(AutopilotOutcome.Conflict, "The request has no hardware hash. Ask the technician to submit the device again.");
        }

        var config = await _config.GetAsync(ct);
        string? groupTag = null;
        if (decision.GroupTagDefinitionId is { } definitionId)
        {
            var definition = await _groupTags.GetAsync(definitionId, ct);
            if (definition is null)
            {
                return AutopilotResult.Fail<AutopilotRegistrationRequest>(AutopilotOutcome.Invalid, "The selected group tag no longer exists.");
            }

            var (locationName, region, country) = await ResolveLocationAsync(entry.Request, ct);
            var option = ToOption(definition, locationName, region, country);
            if (option.ResolvedValue is null)
            {
                return AutopilotResult.Fail<AutopilotRegistrationRequest>(AutopilotOutcome.Invalid, option.UnavailableReason ?? "The selected group tag cannot be used for this device.");
            }
            groupTag = option.ResolvedValue;
        }
        else if (config.AutopilotGroupTagRequired)
        {
            return AutopilotResult.Fail<AutopilotRegistrationRequest>(AutopilotOutcome.Invalid, "A group tag is required.");
        }

        var now = _time.GetUtcNow();
        var claimed = entry.Request with
        {
            State = AutopilotRegistrationState.Importing,
            GroupTag = groupTag,
            GroupTagDefinitionId = decision.GroupTagDefinitionId,
            DecidedByUpn = decision.DecidedByUpn,
            DecidedByObjectId = decision.DecidedByObjectId,
            DecidedAt = now,
            UpdatedAt = now,
            ImportStartedAt = now,
            ImportAttempts = entry.Request.ImportAttempts + 1,
            ImportedIdentityId = null,
            ImportErrorCode = null,
            ImportErrorName = null,
        };

        var etag = await _requests.TryReplaceAsync(claimed, entry.ETag, ct);
        if (etag is null)
        {
            return AutopilotResult.Fail<AutopilotRegistrationRequest>(AutopilotOutcome.Conflict, "Someone else acted on this request. Refresh and try again.");
        }

        LogApproved(_logger, claimed.RequestId, decision.DecidedByUpn, groupTag ?? "(none)");
        return await StartImportAsync(claimed, etag.Value, config, ct);
    }

    public async Task<AutopilotResult<AutopilotRegistrationRequest>> RetryAsync(Guid requestId, AutopilotDecision decision, CancellationToken ct)
    {
        var found = await _requests.GetAsync(requestId, ct);
        if (found is not { } entry)
        {
            return AutopilotResult.Fail<AutopilotRegistrationRequest>(AutopilotOutcome.NotFound, "Request not found.");
        }

        if (entry.Request.State != AutopilotRegistrationState.ImportFailed || string.IsNullOrWhiteSpace(entry.Request.HardwareHash))
        {
            return AutopilotResult.Fail<AutopilotRegistrationRequest>(AutopilotOutcome.Conflict, $"Only a failed import can be retried. The request is {entry.Request.State}.");
        }

        var now = _time.GetUtcNow();
        var claimed = entry.Request with
        {
            State = AutopilotRegistrationState.Importing,
            UpdatedAt = now,
            ImportStartedAt = now,
            ImportAttempts = entry.Request.ImportAttempts + 1,
            ImportedIdentityId = null,
            ImportErrorCode = null,
            ImportErrorName = null,
        };

        var etag = await _requests.TryReplaceAsync(claimed, entry.ETag, ct);
        if (etag is null)
        {
            return AutopilotResult.Fail<AutopilotRegistrationRequest>(AutopilotOutcome.Conflict, "Someone else acted on this request. Refresh and try again.");
        }

        LogRetried(_logger, claimed.RequestId, decision.DecidedByUpn);
        return await StartImportAsync(claimed, etag.Value, await _config.GetAsync(ct), ct);
    }

    public async Task<AutopilotResult<AutopilotRegistrationRequest>> RejectAsync(Guid requestId, AutopilotDecision decision, CancellationToken ct)
    {
        var found = await _requests.GetAsync(requestId, ct);
        if (found is not { } entry)
        {
            return AutopilotResult.Fail<AutopilotRegistrationRequest>(AutopilotOutcome.NotFound, "Request not found.");
        }

        if (entry.Request.State is not (AutopilotRegistrationState.PendingApproval or AutopilotRegistrationState.ImportFailed))
        {
            return AutopilotResult.Fail<AutopilotRegistrationRequest>(AutopilotOutcome.Conflict, $"The request is {entry.Request.State} and can no longer be rejected.");
        }

        var reason = decision.Reason?.Trim();
        if (reason is { Length: > MaxReasonLength })
        {
            return AutopilotResult.Fail<AutopilotRegistrationRequest>(AutopilotOutcome.Invalid, $"The reason is limited to {MaxReasonLength} characters.");
        }

        var now = _time.GetUtcNow();
        var rejected = entry.Request with
        {
            State = AutopilotRegistrationState.Rejected,
            HardwareHash = null,
            RejectionReason = string.IsNullOrEmpty(reason) ? null : reason,
            DecidedByUpn = decision.DecidedByUpn,
            DecidedByObjectId = decision.DecidedByObjectId,
            DecidedAt = now,
            UpdatedAt = now,
        };

        if (await _requests.TryReplaceAsync(rejected, entry.ETag, ct) is null)
        {
            return AutopilotResult.Fail<AutopilotRegistrationRequest>(AutopilotOutcome.Conflict, "Someone else acted on this request. Refresh and try again.");
        }

        LogRejected(_logger, rejected.RequestId, decision.DecidedByUpn);
        return AutopilotResult.Ok(rejected);
    }

    // ── Background processing ────────────────────────────────────────────────

    /// <summary>
    /// Advances in-flight imports, expires undecided requests and deletes handled requests older
    /// than the retention period. Returns how many requests changed or were deleted.
    /// </summary>
    public async Task<int> ProcessAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var config = await _config.GetAsync(ct);
        var retentionCutoff = now.AddDays(-config.AutopilotRetentionDays);
        var changed = 0;
        var purged = 0;
        var anyImported = false;

        var open = new List<(AutopilotRegistrationRequest Request, ETag ETag)>();
        var stale = new List<(AutopilotRegistrationRequest Request, ETag ETag)>();
        await foreach (var entry in _requests.ListAsync(ct))
        {
            if (!AutopilotRegistrationRequest.IsTerminal(entry.Request.State))
            {
                open.Add(entry);
            }
            else if (entry.Request.UpdatedAt < retentionCutoff)
            {
                stale.Add(entry);
            }
        }

        foreach (var (request, etag) in stale)
        {
            if (await _requests.TryDeleteAsync(request.RequestId, etag, ct))
            {
                purged++;
            }
        }

        if (purged > 0)
        {
            LogPurged(_logger, purged, config.AutopilotRetentionDays);
        }

        foreach (var (request, etag) in open)
        {
            AutopilotRegistrationRequest? updated = request.State switch
            {
                AutopilotRegistrationState.PendingApproval or AutopilotRegistrationState.ImportFailed when request.ExpiresAt <= now =>
                    request with { State = AutopilotRegistrationState.Expired, HardwareHash = null, UpdatedAt = now },
                AutopilotRegistrationState.Importing => await AdvanceImportAsync(request, config, now, ct),
                _ => null,
            };

            if (updated is null || await _requests.TryReplaceAsync(updated, etag, ct) is null)
            {
                continue;
            }

            changed++;
            anyImported |= updated.State == AutopilotRegistrationState.Imported;
            LogStateAdvanced(_logger, updated.RequestId, request.State, updated.State);
        }

        if (anyImported)
        {
            try
            {
                await _graph.SyncAsync(ct);
            }
            catch (Exception ex) when (IsGraphFailure(ex))
            {
                // Intune throttles sync to roughly once per ten minutes; the device still appears on the next scheduled sync.
                LogSyncFailed(_logger, ex);
            }
        }

        return changed + purged;
    }

    private async Task<AutopilotRegistrationRequest?> AdvanceImportAsync(AutopilotRegistrationRequest request, PortalConfiguration config, DateTimeOffset now, CancellationToken ct)
    {
        var started = request.ImportStartedAt ?? request.UpdatedAt;
        if (request.ImportedIdentityId is null)
        {
            // The approval request died between claiming the request and storing the Graph import id.
            return now - started > ImportStartTimeout
                ? Failed(request, config, now, null, "The import did not start. Retry the import.")
                : null;
        }

        AutopilotImportStatus status;
        try
        {
            status = await _graph.GetImportStatusAsync(request.ImportedIdentityId, ct);
        }
        catch (Exception ex) when (IsGraphFailure(ex))
        {
            LogImportStatusFailed(_logger, ex, request.RequestId);
            return now - started > ImportTimeout
                ? Failed(request, config, now, null, "Intune did not report the import result in time.")
                : null;
        }

        if (status.IsComplete)
        {
            await TryDeleteImportAsync(request.ImportedIdentityId, ct);
            return request with
            {
                State = AutopilotRegistrationState.Imported,
                HardwareHash = null,
                ImportCompletedAt = now,
                ImportedIdentityId = null,
                UpdatedAt = now,
            };
        }

        if (status.IsError)
        {
            await TryDeleteImportAsync(request.ImportedIdentityId, ct);

            // Intune reports a device that is already registered in this tenant as an import error; it is the outcome the approver wanted.
            if (string.Equals(status.ErrorName, "ZtdDeviceAlreadyAssigned", StringComparison.OrdinalIgnoreCase))
            {
                return request with
                {
                    State = AutopilotRegistrationState.AlreadyRegistered,
                    HardwareHash = null,
                    ImportedIdentityId = null,
                    ImportCompletedAt = now,
                    UpdatedAt = now,
                };
            }

            return Failed(request, config, now, status.ErrorCode, status.ErrorName ?? "Intune rejected the import.");
        }

        if (now - started > ImportTimeout)
        {
            await TryDeleteImportAsync(request.ImportedIdentityId, ct);
            return Failed(request, config, now, null, "Intune did not finish processing the import in time.");
        }

        return null;
    }

    private async Task<AutopilotResult<AutopilotRegistrationRequest>> StartImportAsync(AutopilotRegistrationRequest claimed, ETag etag, PortalConfiguration config, CancellationToken ct)
    {
        AutopilotRegistrationRequest next;
        AutopilotResult<AutopilotRegistrationRequest> result;
        try
        {
            var importId = await _graph.ImportAsync(claimed.SerialNumber, claimed.HardwareHash!, claimed.GroupTag, ct);
            next = claimed with { ImportedIdentityId = importId };
            result = AutopilotResult.Ok(next);
        }
        catch (Exception ex) when (IsGraphFailure(ex))
        {
            LogImportFailed(_logger, ex, claimed.RequestId);
            next = Failed(claimed, config, _time.GetUtcNow(), null, Truncate(ex.Message, MaxReasonLength));
            result = new AutopilotResult<AutopilotRegistrationRequest>(AutopilotOutcome.GraphFailed, next, next.ImportErrorName);
        }

        // If this write loses a race the timer reports the start timeout, which the approver can retry.
        await _requests.TryReplaceAsync(next, etag, ct);
        return result;
    }

    private static AutopilotRegistrationRequest Failed(AutopilotRegistrationRequest request, PortalConfiguration config, DateTimeOffset now, string? code, string name) =>
        request with
        {
            State = AutopilotRegistrationState.ImportFailed,
            ImportedIdentityId = null,
            ImportErrorCode = code,
            ImportErrorName = name,
            UpdatedAt = now,
            ExpiresAt = now.AddDays(config.AutopilotPendingExpiryDays),
        };

    private async Task TryDeleteImportAsync(string importedIdentityId, CancellationToken ct)
    {
        try
        {
            await _graph.DeleteImportAsync(importedIdentityId, ct);
        }
        catch (Exception ex) when (IsGraphFailure(ex))
        {
            // A leftover import record is harmless; Intune purges processed records on its own.
            LogImportCleanupFailed(_logger, ex, importedIdentityId);
        }
    }

    private async Task<(string? Name, string? Region, string? Country)> ResolveLocationAsync(AutopilotRegistrationRequest request, CancellationToken ct)
    {
        var location = request.LocationId is { } id ? await _locations.GetByIdAsync(id, ct) : null;
        return location is null
            ? (request.LocationName, null, null)
            : (location.Name, location.Region, location.CountryCode);
    }

    internal static AutopilotGroupTagOption ToOption(AutopilotGroupTagDefinition definition, string? locationName, string? region, string? country)
    {
        string? value;
        string? reason;
        if (definition.Kind == AutopilotGroupTagKind.Static)
        {
            reason = AutopilotGroupTagTemplate.ValidateResolvedValue(definition.Value);
            value = reason is null ? definition.Value : null;
        }
        else
        {
            (value, reason) = AutopilotGroupTagTemplate.Resolve(definition.Value, locationName, region, country);
        }

        return new AutopilotGroupTagOption
        {
            DefinitionId = definition.Id,
            Name = definition.Name,
            Kind = definition.Kind,
            Value = definition.Value,
            ResolvedValue = value,
            UnavailableReason = reason,
        };
    }

    internal static string? ValidateSubmission(AutopilotHashSubmission submission)
    {
        if (string.IsNullOrWhiteSpace(submission.SerialNumber) || string.Equals(submission.SerialNumber.Trim(), "UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            return "The device serial number could not be read. Autopilot requires a serial number.";
        }

        if (string.IsNullOrWhiteSpace(submission.Manufacturer) || string.IsNullOrWhiteSpace(submission.Model))
        {
            return "Manufacturer and model are required.";
        }

        var hash = submission.HardwareHash?.Trim();
        if (string.IsNullOrEmpty(hash) || hash.Length > MaxHashLength)
        {
            return "The hardware hash is missing or too large.";
        }

        var buffer = new byte[hash.Length];
        return Convert.TryFromBase64String(hash, buffer, out _) ? null : "The hardware hash is not valid Base64.";
    }

    internal static string HashStatusToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    internal static bool StatusTokenMatches(string? storedHash, string? presentedToken)
    {
        if (string.IsNullOrEmpty(storedHash) || string.IsNullOrEmpty(presentedToken))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash),
            Encoding.ASCII.GetBytes(HashStatusToken(presentedToken)));
    }

    private static string NewStatusToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string NewReferenceCode() =>
        "AP-" + RandomNumberGenerator.GetString(ReferenceAlphabet, 5);

    private static AutopilotSubmissionResponse ToSubmissionResponse(AutopilotRegistrationRequest request, string statusToken) => new()
    {
        RequestId = request.RequestId,
        ReferenceCode = request.ReferenceCode,
        State = request.State,
        StatusToken = statusToken,
        ExpiresAt = request.ExpiresAt,
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static bool IsGraphFailure(Exception ex) =>
        ex is AutopilotGraphException or HttpRequestException or Azure.Identity.AuthenticationFailedException or RequestFailedException;

    [LoggerMessage(Level = LogLevel.Information, Message = "Autopilot request {RequestId} ({ReferenceCode}) submitted in state {State}.")]
    private static partial void LogSubmitted(ILogger logger, Guid requestId, string referenceCode, AutopilotRegistrationState state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Autopilot request {RequestId} ({ReferenceCode}) resubmitted by the device.")]
    private static partial void LogResubmitted(ILogger logger, Guid requestId, string referenceCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check whether serial {SerialNumber} is already registered in Autopilot.")]
    private static partial void LogRegisteredLookupFailed(ILogger logger, Exception ex, string serialNumber);

    [LoggerMessage(Level = LogLevel.Information, Message = "Autopilot request {RequestId} approved by {DecidedBy} with group tag {GroupTag}.")]
    private static partial void LogApproved(ILogger logger, Guid requestId, string decidedBy, string groupTag);

    [LoggerMessage(Level = LogLevel.Information, Message = "Autopilot request {RequestId} import retried by {DecidedBy}.")]
    private static partial void LogRetried(ILogger logger, Guid requestId, string decidedBy);

    [LoggerMessage(Level = LogLevel.Information, Message = "Autopilot request {RequestId} rejected by {DecidedBy}.")]
    private static partial void LogRejected(ILogger logger, Guid requestId, string decidedBy);

    [LoggerMessage(Level = LogLevel.Error, Message = "Autopilot import for request {RequestId} failed to start.")]
    private static partial void LogImportFailed(ILogger logger, Exception ex, Guid requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the Autopilot import status for request {RequestId}.")]
    private static partial void LogImportStatusFailed(ILogger logger, Exception ex, Guid requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete Autopilot import record {ImportedIdentityId}.")]
    private static partial void LogImportCleanupFailed(ILogger logger, Exception ex, string importedIdentityId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Autopilot device sync request failed.")]
    private static partial void LogSyncFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Autopilot request {RequestId} moved from {From} to {To}.")]
    private static partial void LogStateAdvanced(ILogger logger, Guid requestId, AutopilotRegistrationState from, AutopilotRegistrationState to);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} handled Autopilot requests older than the {RetentionDays}-day retention period.")]
    private static partial void LogPurged(ILogger logger, int count, int retentionDays);
}
