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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Unit;

/// <summary>
/// After a certificate rotation, media still on the previous certificate fetches Wi-Fi networks and then
/// restarts into a boot image with the new one, so the envelope it stores must open with both.
/// </summary>
public sealed class ManagedWifiRotationTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly X509Certificate2 _previous = CreateCertificate();
    private readonly X509Certificate2 _active = CreateCertificate();

    public void Dispose()
    {
        _previous.Dispose();
        _active.Dispose();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=CloudImaging-BootMedia-Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
    }

    private ManagedWifiNetworkFunctions CreateFunction()
    {
        var core = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"locationId":null,"networks":[{"ssid":"Contoso-Imaging","hidden":false,"security":"Wpa3Personal","password":"correct horse"}]}""", Encoding.UTF8, "application/json"),
        });
        var accepted = new List<AcceptedBootMediaCertificate>
        {
            new(_active.Thumbprint, true, null, Convert.ToBase64String(_active.Export(X509ContentType.Cert))),
            new(_previous.Thumbprint, false, DateTimeOffset.UtcNow.AddDays(14), Convert.ToBase64String(_previous.Export(X509ContentType.Cert))),
        };
        var cache = new BootMediaCertificateThumbprintCache(_ => Task.FromResult<IReadOnlyList<AcceptedBootMediaCertificate>>(accepted), NullLogger<BootMediaCertificateThumbprintCache>.Instance);
        var nonceStore = new DeviceSessionNonceStore((_, _, _) => Task.FromResult(true), NullLogger<DeviceSessionNonceStore>.Instance);
        return new ManagedWifiNetworkFunctions(new ImagingCoreClient(new HttpClient(core) { BaseAddress = new Uri("https://core.internal") }), nonceStore, cache, new ConfigurationBuilder().Build(), NullLogger<ManagedWifiNetworkFunctions>.Instance);
    }

    private static async Task<byte[]> FetchAsync(ManagedWifiNetworkFunctions function, X509Certificate2 presented)
    {
        var timestampUtc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var rsa = presented.GetRSAPrivateKey()!;
        var signature = rsa.SignData(DevicePayloadSignature.BuildWifiChallenge("SN-1", null, timestampUtc, nonce), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var request = new ManagedWifiRequest
        {
            SerialNumber = "SN-1",
            ProofOfPossession = new DeviceProofOfPossession { Nonce = nonce, TimestampUtc = timestampUtc, Signature = Convert.ToBase64String(signature) },
        };

        var context = FunctionHttp.Context(new Dictionary<object, object> { [MtlsCertificateValidationMiddleware.ClientCertificateItemKey] = presented });
        var response = await function.GetManagedWifiNetworks(FunctionHttp.Request(context, "POST", "https://gateway/api/v1/wifi-networks", JsonSerializer.Serialize(request, WebJson)), context);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Body.Position = 0;
        using var buffer = new MemoryStream();
        await response.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public async Task MediaOnThePreviousCertificate_GetsNetworksTheNewBootImageCanRead()
    {
        var envelope = await FetchAsync(CreateFunction(), _previous);

        ManagedWifiEnvelope.Decrypt(envelope, _previous).Networks.Single().Password.Should().Be("correct horse");
        ManagedWifiEnvelope.Decrypt(envelope, _active).Networks.Single().Password.Should().Be("correct horse");
    }

    [Fact]
    public async Task MediaOnTheActiveCertificate_GetsASingleRecipientEnvelope()
    {
        var envelope = await FetchAsync(CreateFunction(), _active);

        Encoding.UTF8.GetString(envelope).Should().Contain("cloudimaging-wifi/1");
        ManagedWifiEnvelope.Decrypt(envelope, _active).Networks.Should().ContainSingle();
        FluentActions.Invoking(() => ManagedWifiEnvelope.Decrypt(envelope, _previous)).Should().Throw<ManagedWifiEnvelopeException>();
    }
}
