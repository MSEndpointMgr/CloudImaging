using CloudImaging.Client.Services;
using CloudImaging.Contracts.Models;
using FluentAssertions;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Unit tests for <see cref="DeviceGatewayApiClient.GetLatestBootImageAsync"/> (T071b, FR-059a).
/// </summary>
public sealed class DeviceGatewayApiClientLatestBootImageTests
{
    private static readonly X509Certificate2 Certificate = CreateCertificate();

    private static DeviceGatewayApiClient Client(FakeHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") }, Certificate);

    [Fact]
    public async Task GetLatestBootImageAsync_ReturnsParsedInfo_OnSuccessResponse()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"version":"2.5.0","sha256Hash":"deadbeef","sasTokenUrl":"https://sas.example.com/x"}""",
                System.Text.Encoding.UTF8, "application/json"),
        });

        var result = await Client(handler).GetLatestBootImageAsync();

        result.Should().NotBeNull();
        result!.Version.Should().Be("2.5.0");
        result.Sha256Hash.Should().Be("deadbeef");
        result.SasTokenUrl.Should().Be("https://sas.example.com/x");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetLatestBootImageAsync_ReturnsNull_OnNonSuccessResponse(HttpStatusCode statusCode)
    {
        // Never throws: the self-update check treats any failure as "nothing to update", so startup is not blocked.
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(statusCode));

        var result = await Client(handler).GetLatestBootImageAsync();

        result.Should().BeNull();
        handler.Requests.Should().ContainSingle("there is no unsigned check to fall back to");
    }

    [Fact]
    public async Task WithoutTheBootMediaCertificate_NothingIsAsked()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });

        (await client.GetLatestBootImageAsync()).Should().BeNull();
        handler.Requests.Should().BeEmpty("the check cannot be signed");
    }

    [Fact]
    public async Task GetLatestBootImageAsync_Treats204AsNoUpdate()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = await Client(handler).GetLatestBootImageAsync(MachineArchitecture.X64, Guid.NewGuid());

        result.Should().BeNull("204 means this stick runs a pre-production image that must not be replaced");
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string PathAndQuery, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }

    // Signed check

    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Cloud Imaging Boot Media Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static HttpResponseMessage Latest(string version) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"version":"{{version}}","sha256Hash":"deadbeef","sasTokenUrl":"https://sas.example.com/x"}""", System.Text.Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task WithTheBootMediaCertificate_TheCheckIsAPostSignedOverWhatItAsksFor()
    {
        using var certificate = CreateCertificate();
        var handler = new FakeHttpMessageHandler(_ => Latest("3.0"));
        var client = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") }, certificate);
        var current = Guid.NewGuid();

        var result = await client.GetLatestBootImageAsync(MachineArchitecture.Arm64, current, BootImageBase.WindowsRE, "SN-42");

        result!.Version.Should().Be("3.0");
        var sent = handler.Requests.Single();
        sent.Method.Should().Be(HttpMethod.Post);
        sent.PathAndQuery.Should().Be("/api/v1/boot-image/latest", "the signed check carries everything in its body");
        var request = System.Text.Json.JsonSerializer.Deserialize<LatestBootImageRequest>(sent.Body!, WebJson)!;
        request.SerialNumber.Should().Be("SN-42");
        request.Architecture.Should().Be(MachineArchitecture.Arm64);
        request.Base.Should().Be(BootImageBase.WindowsRE);
        request.CurrentBootImageId.Should().Be(current);
        var proof = request.ProofOfPossession!;
        var challenge = DevicePayloadSignature.BuildBootImageChallenge("SN-42", MachineArchitecture.Arm64, BootImageBase.WindowsRE, current, proof.TimestampUtc, proof.Nonce);
        using var rsa = certificate.GetRSAPublicKey()!;
        rsa.VerifyData(challenge, Convert.FromBase64String(proof.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Should().BeTrue();
    }
}
