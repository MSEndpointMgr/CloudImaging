using System.Security.Cryptography;
using System.Text;

namespace CloudImaging.DeviceGatewayApi.Security;

/// <summary>
/// Generates and validates device-session bearer tokens (FR-010, FR-014, June 15 2026 clarification).
/// The token is an opaque, high-entropy credential bound to a specific session ID.
/// It is never shown to technicians and is distinct from the one-time pairing passcode.
/// Registered as a singleton in DI so that Function endpoints can inject it.
/// </summary>
public sealed class DeviceSessionTokenService
{
    private const int TokenByteLength = 32; // 256-bit token

    /// <summary>Default token lifetime — 24 hours.</summary>
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// Issues a new opaque device-session token for the given session using the default lifetime.
    /// Returns the plain token (to return to the device) — the caller must store the hash at rest.
    /// </summary>
    public string IssueToken(Guid sessionId) => Issue(sessionId, DefaultLifetime).PlainToken;

    /// <summary>
    /// Issues a new opaque device-session token for the given session.
    /// Returns (plainToken, tokenHash, expiry). Only the hash is persisted.
    /// </summary>
    public (string PlainToken, string TokenHash, DateTimeOffset ExpiresAt) Issue(
        Guid sessionId,
        TimeSpan lifetime)
    {
        Span<byte> randomBytes = stackalloc byte[TokenByteLength];
        RandomNumberGenerator.Fill(randomBytes);

        // Token format: base64url(sessionId_bytes || random_bytes) for implicit binding
        var sessionBytes = sessionId.ToByteArray();
        var combined = new byte[sessionBytes.Length + TokenByteLength];
        sessionBytes.CopyTo(combined, 0);
        randomBytes.CopyTo(combined.AsSpan(sessionBytes.Length));

        var plainToken = Convert.ToBase64String(combined)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var tokenHash = HashToken(plainToken);
        var expiresAt = DateTimeOffset.UtcNow + lifetime;

        return (plainToken, tokenHash, expiresAt);
    }

    /// <summary>
    /// Validates a bearer token from an HTTP request header.
    /// Returns the session ID embedded in the token if the hash matches.
    /// </summary>
    public static bool TryValidate(string plainToken, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(plainToken) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        var candidateHash = HashToken(plainToken);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidateHash),
            Encoding.UTF8.GetBytes(storedHash));
    }

    /// <summary>
    /// Extracts the session ID from a plain token without validation.
    /// Call only after <see cref="TryValidate"/> succeeds.
    /// </summary>
    public static Guid? ExtractSessionId(string plainToken)
    {
        try
        {
            var padded = plainToken.Replace('-', '+').Replace('_', '/');
            var mod4 = padded.Length % 4;
            if (mod4 > 0) padded += new string('=', 4 - mod4);
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length < 16) return null;
            return new Guid(bytes[..16]);
        }
        catch { return null; }
    }

    private static string HashToken(string plainToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainToken))).ToLowerInvariant();
}
