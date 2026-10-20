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
/// </summary>
public static class ManagedWifiEnvelope
{
    /// <summary>File name on the root of the USB BOOT partition, next to <see cref="UsbPreparationManifest.FileName"/>.</summary>
    public const string FileName = "cloudimaging-wifi.json";

    private const string Format = "cloudimaging-wifi/1";
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
    /// Decrypts an envelope with the private key of <paramref name="certificate"/>. Throws
    /// <see cref="ManagedWifiEnvelopeException"/> when the bytes are not an envelope for this certificate.
    /// </summary>
    public static ManagedWifiNetworkSet Decrypt(ReadOnlySpan<byte> envelope, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        EnvelopeDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<EnvelopeDocument>(envelope, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ManagedWifiEnvelopeException("The Wi-Fi file is not readable.", ex);
        }
        if (document is null || document.Format != Format)
        {
            throw new ManagedWifiEnvelopeException("The Wi-Fi file has an unknown format.");
        }
        if (!string.Equals(document.RecipientThumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new ManagedWifiEnvelopeException("The Wi-Fi file was saved for a different boot media certificate.");
        }

        using var rsa = certificate.GetRSAPrivateKey() ?? throw new ManagedWifiEnvelopeException("The boot media certificate has no private key.");
        byte[]? key = null;
        byte[]? plaintext = null;
        try
        {
            key = rsa.Decrypt(Convert.FromBase64String(document.EncryptedKey), RSAEncryptionPadding.OaepSHA256);
            var ciphertext = Convert.FromBase64String(document.Ciphertext);
            plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(Convert.FromBase64String(document.Nonce), ciphertext, Convert.FromBase64String(document.Tag), plaintext, AssociatedData(certificate.Thumbprint));
            return JsonSerializer.Deserialize<ManagedWifiNetworkSet>(plaintext, JsonOptions)
                ?? throw new ManagedWifiEnvelopeException("The Wi-Fi file is empty.");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
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

    private sealed record EnvelopeDocument(string Format, string RecipientThumbprint, string EncryptedKey, string Nonce, string Ciphertext, string Tag);
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
