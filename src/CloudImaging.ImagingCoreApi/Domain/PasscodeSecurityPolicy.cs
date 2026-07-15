using System.Security.Cryptography;
using System.Text;

namespace CloudImaging.ImagingCoreApi.Domain;

/// <summary>
/// Passcode security policy for session coupling (FR-021, clarification June 15 2026).
/// The 6-character alphanumeric passcode is a one-time pairing code stored as a hash at rest.
/// </summary>
public static class PasscodeSecurityPolicy
{
    /// <summary>Allowed characters: uppercase A-Z and 0-9, excluding I, O, 1, 0 for readability.</summary>
    private static readonly char[] AllowedChars =
        "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    private const int PasscodeLength = 6;

    /// <summary>
    /// Generates a cryptographically random 6-character alphanumeric passcode.
    /// </summary>
    public static string GeneratePasscode()
    {
        var buffer = new char[PasscodeLength];
        using var rng = RandomNumberGenerator.Create();
        var randomBytes = new byte[PasscodeLength];
        rng.GetBytes(randomBytes);

        for (int i = 0; i < PasscodeLength; i++)
        {
            buffer[i] = AllowedChars[randomBytes[i] % AllowedChars.Length];
        }

        return new string(buffer);
    }

    /// <summary>
    /// Hashes a passcode using SHA-256 for at-rest storage.
    /// The raw passcode is never persisted (FR-010, June 15 clarification).
    /// </summary>
    public static string HashPasscode(string passcode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passcode);
        var normalized = passcode.ToUpperInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Constant-time comparison of a submitted passcode against a stored hash.
    /// Case-insensitive (FR-032: portal input is case-insensitive).
    /// </summary>
    public static bool VerifyPasscode(string submitted, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(submitted) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        var candidateHash = HashPasscode(submitted);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidateHash),
            Encoding.UTF8.GetBytes(storedHash));
    }

    /// <summary>Returns true when the collision-retry limit for passcode generation is not exceeded.</summary>
    public const int MaxCollisionRetries = 3;
}
