using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// Encrypts a <see cref="ManagedWifiNetworkSet"/> to the boot media certificate, so Wi-Fi passwords
/// can sit on USB media and only a boot image carrying that certificate's private key can read them.
/// Media Builder and the Device Gateway produce the same bytes; the Client decrypts them and writes
/// fresh ones from the gateway straight to the USB. AES-256-GCM content key, wrapped with RSA-OAEP-SHA256.
/// While a rotated certificate is still accepted the gateway encrypts to both certificates, so the
/// file stays readable after the media updates itself to a boot image with the new one.
/// </summary>
public static class ManagedWifiEnvelope
{
    /// <summary>File name on the root of the USB BOOT partition, next to <see cref="UsbPreparationManifest.FileName"/>.</summary>
    public const string FileName = "cloudimaging-wifi.json";

    private const string Format = "cloudimaging-wifi/1";
    private const string MultiFormat = "cloudimaging-wifi/2";
    private const int TagSize = 16;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Encrypts <paramref name="set"/> to the public key of <paramref name="recipient"/>.</summary>
    public static byte[] Encrypt(ManagedWifiNetworkSet set, X509Certificate2 recipient)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(recipient);

        using var rsa = recipient.GetRSAPublicKey() ?? throw new InvalidOperationException("The boot media certificate has no RSA public key.");
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(set, JsonOptions);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(recipient.Thumbprint));
            var document = new EnvelopeDocument(
                Format,
                recipient.Thumbprint,
                Convert.ToBase64String(rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256)),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext),
                Convert.ToBase64String(tag));
            return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Encrypts <paramref name="set"/> so any one of <paramref name="recipients"/> can read it. One
    /// recipient gives the same format as <see cref="Encrypt(ManagedWifiNetworkSet, X509Certificate2)"/>.
    /// </summary>
    public static byte[] Encrypt(ManagedWifiNetworkSet set, IReadOnlyCollection<X509Certificate2> recipients)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(recipients);

        var distinct = recipients.DistinctBy(r => r.Thumbprint, StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0)
        {
            throw new ArgumentException("At least one recipient is required.", nameof(recipients));
        }
        if (distinct.Count == 1)
        {
            return Encrypt(set, distinct[0]);
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(set, JsonOptions);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        try
        {
            var wrapped = distinct.Select(recipient =>
            {
                using var rsa = recipient.GetRSAPublicKey() ?? throw new InvalidOperationException("The boot media certificate has no RSA public key.");
                return new RecipientKey(recipient.Thumbprint, Convert.ToBase64String(rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256)));
            }).ToList();
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, MultiAssociatedData(wrapped.Select(r => r.Thumbprint)));
            var document = new MultiEnvelopeDocument(
                MultiFormat,
                wrapped,
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext),
                Convert.ToBase64String(tag));
            return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Decrypts an envelope with the private key of <paramref name="certificate"/>. Throws
    /// <see cref="ManagedWifiEnvelopeException"/> when the bytes are not an envelope for this certificate.
    /// </summary>
    public static ManagedWifiNetworkSet Decrypt(ReadOnlySpan<byte> envelope, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        string? format;
        string encryptedKey, nonce, ciphertextText, tag;
        byte[] associatedData;
        try
        {
            format = JsonSerializer.Deserialize<FormatProbe>(envelope, JsonOptions)?.Format;
            if (format == MultiFormat)
            {
                var multi = JsonSerializer.Deserialize<MultiEnvelopeDocument>(envelope, JsonOptions);
                if (multi?.Recipients is null)
                {
                    throw new ManagedWifiEnvelopeException("The Wi-Fi file is not readable.");
                }
                var mine = multi.Recipients.FirstOrDefault(r => string.Equals(r?.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ManagedWifiEnvelopeException("The Wi-Fi file was saved for a different boot media certificate.");
                (encryptedKey, nonce, ciphertextText, tag) = (mine.EncryptedKey, multi.Nonce, multi.Ciphertext, multi.Tag);
                associatedData = MultiAssociatedData(multi.Recipients.Select(r => r?.Thumbprint ?? string.Empty));
            }
            else if (format == Format)
            {
                var document = JsonSerializer.Deserialize<EnvelopeDocument>(envelope, JsonOptions)
                    ?? throw new ManagedWifiEnvelopeException("The Wi-Fi file is not readable.");
                if (!string.Equals(document.RecipientThumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ManagedWifiEnvelopeException("The Wi-Fi file was saved for a different boot media certificate.");
                }
                (encryptedKey, nonce, ciphertextText, tag) = (document.EncryptedKey, document.Nonce, document.Ciphertext, document.Tag);
                associatedData = AssociatedData(certificate.Thumbprint);
            }
            else
            {
                throw new ManagedWifiEnvelopeException("The Wi-Fi file has an unknown format.");
            }
        }
        catch (JsonException ex)
        {
            throw new ManagedWifiEnvelopeException("The Wi-Fi file is not readable.", ex);
        }

        using var rsa = certificate.GetRSAPrivateKey() ?? throw new ManagedWifiEnvelopeException("The boot media certificate has no private key.");
        byte[]? key = null;
        byte[]? plaintext = null;
        try
        {
            key = rsa.Decrypt(Convert.FromBase64String(encryptedKey), RSAEncryptionPadding.OaepSHA256);
            var ciphertext = Convert.FromBase64String(ciphertextText);
            plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(Convert.FromBase64String(nonce), ciphertext, Convert.FromBase64String(tag), plaintext, associatedData);
            return JsonSerializer.Deserialize<ManagedWifiNetworkSet>(plaintext, JsonOptions)
                ?? throw new ManagedWifiEnvelopeException("The Wi-Fi file is empty.");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            throw new ManagedWifiEnvelopeException("The Wi-Fi file could not be decrypted.", ex);
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static byte[] AssociatedData(string thumbprint) =>
        Encoding.UTF8.GetBytes($"{Format}\n{thumbprint.ToUpperInvariant()}");

    // Binds the ciphertext to the whole recipient list, so a recipient cannot be dropped or swapped.
    private static byte[] MultiAssociatedData(IEnumerable<string> thumbprints) =>
        Encoding.UTF8.GetBytes($"{MultiFormat}\n{string.Join('\n', thumbprints.Select(t => t.ToUpperInvariant()).Order(StringComparer.Ordinal))}");

    private sealed record FormatProbe(string? Format);

    private sealed record EnvelopeDocument(string Format, string RecipientThumbprint, string EncryptedKey, string Nonce, string Ciphertext, string Tag);

    private sealed record RecipientKey(string Thumbprint, string EncryptedKey);

    private sealed record MultiEnvelopeDocument(string Format, IReadOnlyList<RecipientKey> Recipients, string Nonce, string Ciphertext, string Tag);
}

/// <summary>Thrown when a Wi-Fi envelope cannot be read with the boot media certificate.</summary>
public sealed class ManagedWifiEnvelopeException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ManagedWifiEnvelopeException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ManagedWifiEnvelopeException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public ManagedWifiEnvelopeException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
