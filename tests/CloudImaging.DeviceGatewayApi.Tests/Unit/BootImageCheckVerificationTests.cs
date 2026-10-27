using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.DeviceGatewayApi.Functions;
using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Security;
using CloudImaging.DeviceGatewayApi.Services;
using CloudImaging.DeviceGatewayApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Unit;

/// <summary>
/// The boot image update check answers with a link to a boot image, and every boot image carries the
/// boot media private key, so the check must prove that key, cover what it asks for, and be single-use.
/// </summary>
public sealed class BootImageCheckVerificationTests : IDisposable
{
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly Guid LatestId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly X509Certificate2 _certificate = CreateCertificate();
    private readonly HashSet<string> _usedNonces = new(StringComparer.Ordinal);

    public void Dispose() => _certificate.Dispose();

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=CloudImaging-BootMedia-Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }

    private LatestBootImageRequest Sign(MachineArchitecture signedArchitecture = MachineArchitecture.X64, MachineArchitecture? sentArchitecture = null, Guid? signedCurrent = null, Guid? sentCurrent = null, bool useWifiChallenge = false, DateTimeOffset? timestamp = null)
    {
        var timestampUtc = (timestamp ?? DateTimeOffset.UtcNow).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var challenge = useWifiChallenge
            ? DevicePayloadSignature.BuildWifiChallenge("SN-1", null, timestampUtc, nonce)
            : DevicePayloadSignature.BuildBootImageChallenge("SN-1", signedArchitecture, BootImageBase.WindowsPE, signedCurrent, timestampUtc, nonce);
        using var rsa = _certificate.GetRSAPrivateKey()!;
        return new LatestBootImageRequest
        {
            SerialNumber = "SN-1",
            Architecture = sentArchitecture ?? signedArchitecture,
            CurrentBootImageId = sentCurrent ?? signedCurrent,
            ProofOfPossession = new DeviceProofOfPossession { Nonce = nonce, TimestampUtc = timestampUtc, Signature = Convert.ToBase64String(rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) },
        };
    }

    // Signature

    [Fact]
    public void AValidSignature_IsAccepted()
    {
        DevicePayloadSignatureVerifier.VerifyBootImage(_certificate, Sign(), MaxSkew, DateTimeOffset.UtcNow)
            .Should().Be(DevicePayloadSignatureVerifier.Result.Valid);
    }

    [Fact]
    public void ASwappedArchitecture_IsRejected()
    {
        DevicePayloadSignatureVerifier.VerifyBootImage(_certificate, Sign(MachineArchitecture.X64, MachineArchitecture.Arm64), MaxSkew, DateTimeOffset.UtcNow)
            .Should().Be(DevicePayloadSignatureVerifier.Result.InvalidSignature);
    }

    [Fact]
    public void ASwappedCurrentImage_IsRejected()
    {
        DevicePayloadSignatureVerifier.VerifyBootImage(_certificate, Sign(signedCurrent: Guid.NewGuid(), sentCurrent: Guid.NewGuid()), MaxSkew, DateTimeOffset.UtcNow)
            .Should().Be(DevicePayloadSignatureVerifier.Result.InvalidSignature);
    }

    [Fact]
    public void ASignatureMadeForWifi_CannotBeReused()
    {
        DevicePayloadSignatureVerifier.VerifyBootImage(_certificate, Sign(useWifiChallenge: true), MaxSkew, DateTimeOffset.UtcNow)
            .Should().Be(DevicePayloadSignatureVerifier.Result.InvalidSignature);
    }

    [Fact]
    public void AnOldSignature_HasExpired()
    {
        DevicePayloadSignatureVerifier.VerifyBootImage(_certificate, Sign(timestamp: DateTimeOffset.UtcNow.AddMinutes(-10)), MaxSkew, DateTimeOffset.UtcNow)
            .Should().Be(DevicePayloadSignatureVerifier.Result.Expired);
    }

    [Fact]
    public void AMissingProof_IsRejected()
    {
        DevicePayloadSignatureVerifier.VerifyBootImage(_certificate, new LatestBootImageRequest { SerialNumber = "SN-1" }, MaxSkew, DateTimeOffset.UtcNow)
            .Should().Be(DevicePayloadSignatureVerifier.Result.Missing);
    }

    [Fact]
    public void TheCheck_SkipsSessionTokens_AndHasNoUnsignedVariant()
    {
        DeviceSessionTokenValidationMiddleware.ExemptFunctionNames.Should().Contain("CheckLatestBootImage");
        typeof(GetLatestBootImageFunction).GetMethods()
            .SelectMany(m => m.GetCustomAttributes(typeof(Microsoft.Azure.Functions.Worker.FunctionAttribute), false))
            .Cast<Microsoft.Azure.Functions.Worker.FunctionAttribute>()
            .Select(f => f.Name)
            .Should().Equal(["CheckLatestBootImage"], "an unsigned check would hand the boot media key to anyone with the public certificate");
    }

    // Function

    private (GetLatestBootImageFunction Function, CapturingHandler Core) Create(string catalog = """[{ "bootImageId": "33333333-3333-3333-3333-333333333333", "version": "4.0", "isLatestPublished": true, "architecture": "x64" }]""")
    {
        var core = new CapturingHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/sas", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"sha256Hash":"abc","sasTokenUrl":"https://core/boot.wim?sig=x"}""", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(catalog, Encoding.UTF8, "application/json") });
        var configuration = new ConfigurationBuilder().Build();
        var nonceStore = new DeviceSessionNonceStore((key, _, _) => Task.FromResult(_usedNonces.Add(key)), NullLogger<DeviceSessionNonceStore>.Instance);
        var function = new GetLatestBootImageFunction(new ImagingCoreClient(new HttpClient(core) { BaseAddress = new Uri("https://core.internal") }), nonceStore, configuration, NullLogger<GetLatestBootImageFunction>.Instance);
        return (function, core);
    }

    private Task<HttpResponseData> PostAsync(GetLatestBootImageFunction function, LatestBootImageRequest request)
    {
        var context = FunctionHttp.Context(new Dictionary<object, object> { [MtlsCertificateValidationMiddleware.ClientCertificateItemKey] = _certificate });
        return function.Check(FunctionHttp.Request(context, "POST", "https://gateway/api/v1/boot-image/latest", JsonSerializer.Serialize(request, WebJson)), context);
    }

    [Fact]
    public async Task ASignedCheck_ReturnsTheLatestImage()
    {
        var (function, core) = Create();

        var response = await PostAsync(function, Sign());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.ReadBodyAsync());
        json.RootElement.GetProperty("version").GetString().Should().Be("4.0");
        json.RootElement.GetProperty("bootImageId").GetGuid().Should().Be(LatestId);
        core.Requests.Select(r => r.Request.RequestUri!.AbsolutePath).Should().Equal("/api/internal/boot-images", $"/api/internal/boot-images/{LatestId}/sas");
    }

    [Fact]
    public async Task AReplayedCheck_IsRefused()
    {
        var (function, _) = Create();
        var request = Sign();

        (await PostAsync(function, request)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAsync(function, request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ATamperedCheck_NeverReachesTheCatalog()
    {
        var (function, core) = Create();

        var response = await PostAsync(function, Sign(MachineArchitecture.X64, MachineArchitecture.Arm64));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        core.Requests.Should().BeEmpty("no link may be issued before the signature checks out");
    }

    [Fact]
    public async Task NoPublishedImage_IsNotFound()
    {
        var (function, _) = Create(catalog: "[]");

        var response = await PostAsync(function, Sign());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.GetValues("Content-Type").Should().Contain("application/problem+json");
    }
}
