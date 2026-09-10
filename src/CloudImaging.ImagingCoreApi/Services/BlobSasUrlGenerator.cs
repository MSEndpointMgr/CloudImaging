using Azure.Storage.Blobs;
using Azure.Storage.Sas;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Generates working SAS URLs for blobs when the <see cref="BlobServiceClient"/> is authenticated
/// with a token credential (managed identity / <c>DefaultAzureCredential</c>) rather than an
/// account key (FR-025, security-hardening: no storage account keys anywhere in this solution).
///
/// <see cref="BlobClient.CanGenerateSasUri"/> is <c>false</c> for a token-credential-backed client,
/// because the classic "shared key" SAS signing path requires the account key. The previous code at
/// every SAS call site checked that flag and, when false, silently fell back to returning the BARE
/// blob URL with no signature at all — which 403/409s the moment the caller tries to use it, because
/// every storage account in this solution has public/anonymous access disabled
/// (<c>allowBlobPublicAccess: false</c>, containers <c>publicAccess: 'None'</c>). Found 2026-08-16
/// via a boot image upload failing with "Public access is not permitted on this storage account."
///
/// The fix is a <b>User Delegation SAS</b>: request a short-lived delegation key from Azure AD via
/// <see cref="BlobServiceClient.GetUserDelegationKeyAsync"/> (valid for the managed identity's own
/// Azure AD token), then sign the SAS with that key instead of an account key. This requires the
/// caller's identity to hold a role that includes the
/// <c>Microsoft.Storage/storageAccounts/blobServices/generateUserDelegationKey/action</c> action —
/// already true here, since the Imaging Core API's managed identity has Storage Blob Data Owner on
/// its storage account (see modules/storage.bicep), and that built-in role includes this action.
/// </summary>
public static class BlobSasUrlGenerator
{
    /// <summary>
    /// Returns a URL the caller can use directly against Blob Storage for <paramref name="permissions"/>
    /// on the given blob, valid for <paramref name="expiry"/>. Prefers a classic shared-key SAS when
    /// the client supports it (e.g. local dev with a connection-string-backed client); otherwise signs
    /// with an Azure AD user delegation key.
    /// </summary>
    public static Task<string> GenerateAsync(
        BlobServiceClient blobServiceClient,
        string containerName,
        string blobName,
        BlobSasPermissions permissions,
        TimeSpan expiry,
        CancellationToken cancellationToken = default) =>
        GenerateAsync(blobServiceClient, containerName, blobName, permissions, expiry, null, cancellationToken);

    /// <summary>
    /// As above, but also overrides the Content-Type the blob service returns for this URL (the
    /// SAS <c>rsct</c> parameter). Use it to pin a charset on text blobs so browsers don't guess
    /// an encoding; because the override lives in the signed URL rather than on the blob, it also
    /// applies to blobs that were uploaded with a less specific content type.
    /// </summary>
    public static async Task<string> GenerateAsync(
        BlobServiceClient blobServiceClient,
        string containerName,
        string blobName,
        BlobSasPermissions permissions,
        TimeSpan expiry,
        string? responseContentType,
        CancellationToken cancellationToken = default)
    {
        var blobClient = blobServiceClient.GetBlobContainerClient(containerName).GetBlobClient(blobName);
        var expiresOn = DateTimeOffset.UtcNow + expiry;
        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b",
            ExpiresOn = expiresOn,
        };
        sasBuilder.SetPermissions(permissions);

        if (!string.IsNullOrWhiteSpace(responseContentType))
        {
            sasBuilder.ContentType = responseContentType;
        }

        if (blobClient.CanGenerateSasUri)
        {
            return blobClient.GenerateSasUri(sasBuilder).ToString();
        }

        // Small backdated start time absorbs clock skew between this host and the token's
        // validity window, same convention used for the SAS token's own clock-skew tolerance.
        var keyStart = DateTimeOffset.UtcNow.AddMinutes(-5);
        var userDelegationKey = await blobServiceClient
            .GetUserDelegationKeyAsync(keyStart, expiresOn, cancellationToken)
            .ConfigureAwait(false);

        var sasQuery = sasBuilder.ToSasQueryParameters(userDelegationKey.Value, blobServiceClient.AccountName);
        var uriBuilder = new UriBuilder(blobClient.Uri) { Query = sasQuery.ToString() };
        return uriBuilder.Uri.ToString();
    }
}
