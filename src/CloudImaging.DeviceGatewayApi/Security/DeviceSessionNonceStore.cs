using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Security;

/// <summary>
/// Enforces single-use of the proof-of-possession nonce for session bootstrap (FR-069), closing the
/// replay window that a timestamp+skew check alone leaves open.
///
/// Without this, a signed CreateSession payload captured by an attacker (which already requires
/// defeating TLS/mTLS to obtain) could be replayed verbatim any number of times until its timestamp
/// falls outside the skew window. Recording each accepted nonce makes every signed request strictly
/// single-use, shrinking the replay window to zero.
///
/// The persistence primitive is injected as a delegate so the replay logic is unit-testable without
/// Azure; the production wiring (Program.cs) backs it with an atomic Table Storage insert
/// (<c>AddEntity</c> → HTTP 409 on a duplicate row). Entries only need to outlive the skew window —
/// after that the timestamp check rejects a replay regardless — so <c>ExpiresAt</c> is recorded to
/// let a storage lifecycle/purge reclaim stale rows without affecting correctness.
/// </summary>
public sealed partial class DeviceSessionNonceStore
{
    /// <summary>
    /// Attempts to persist <paramref name="nonceKey"/> as a first-seen entry expiring at
    /// <paramref name="expiresAt"/>. Returns <c>true</c> if newly recorded, or <c>false</c> if the
    /// key already exists (i.e. a replay).
    /// </summary>
    public delegate Task<bool> NonceRegistrar(string nonceKey, DateTimeOffset expiresAt, CancellationToken ct);

    private readonly NonceRegistrar _register;
    private readonly ILogger<DeviceSessionNonceStore> _logger;

    public DeviceSessionNonceStore(NonceRegistrar register, ILogger<DeviceSessionNonceStore> logger)
    {
        _register = register;
        _logger   = logger;
    }

    /// <summary>
    /// Records the nonce as consumed. Returns <c>true</c> on first use (accept the request), or
    /// <c>false</c> if the nonce has already been seen (reject as a replay).
    /// </summary>
    /// <param name="nonce">The raw Base64 nonce from the proof-of-possession block.</param>
    /// <param name="expiresAt">When the recorded entry may be purged (skew window boundary).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> TryConsumeAsync(string nonce, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        string key = HashNonce(nonce);
        bool firstUse = await _register(key, expiresAt, ct);
        if (!firstUse)
        {
            LogReplayDetected(_logger, key);
        }
        return firstUse;
    }

    /// <summary>
    /// Hashes the raw nonce (Base64, which may contain characters invalid in a Table RowKey such as
    /// '/') into an uppercase-hex SHA-256 digest that is both a safe Table key and non-reversible.
    /// </summary>
    public static string HashNonce(string nonce)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
        return Convert.ToHexString(digest);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Proof-of-possession nonce replay detected. NonceKey={NonceKey}")]
    private static partial void LogReplayDetected(ILogger logger, string nonceKey);
}
