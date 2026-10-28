using System.Net;
using System.Text;
using System.Text.Json;
using CloudImaging.OperatorApi.Functions;
using CloudImaging.OperatorApi.Middleware;
using CloudImaging.OperatorApi.Services;
using CloudImaging.OperatorApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>
/// A rotation can keep the replaced boot media certificate accepted for a while; the portal shows that
/// certificate and can end the overlap early.
/// </summary>
public sealed class BootMediaCertificateOverlapContractTests
{
    private static BootMediaCertificateFunctions Create(CapturingHandler core) =>
        new(new ImagingCoreClient(new HttpClient(core) { BaseAddress = new Uri("https://core.internal") }), NullLogger<BootMediaCertificateFunctions>.Instance);

    [Fact]
    public async Task Metadata_IncludesThePreviousCertificateWhileItIsAccepted()
    {
        const string active = """{"thumbprint":"aabb","notBefore":"2026-01-01T00:00:00+00:00","notAfter":"2028-01-01T00:00:00+00:00","isActive":true,"keyVaultSecretName":"boot-media-cert-aabb","previous":{"thumbprint":"ccdd","notAfter":"2027-01-01T00:00:00+00:00","acceptedUntil":"2026-02-01T00:00:00+00:00"}}""";
        var core = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(active, Encoding.UTF8, "application/json") });
        var context = FunctionHttp.Context();

        var response = await Create(core).GetCertificateMetadata(FunctionHttp.Request(context, "GET", "https://operator/api/bootmedia/certificate/metadata"), context);

        using var json = JsonDocument.Parse(await response.ReadBodyAsync());
        var previous = json.RootElement.GetProperty("previous");
        previous.GetProperty("thumbprintDisplay").GetString().Should().Be("CCDD");
        previous.GetProperty("acceptedUntil").GetString().Should().Be("2026-02-01T00:00:00+00:00");
    }

    [Fact]
    public async Task Metadata_HasNoPreviousCertificate_AfterTheOverlap()
    {
        const string active = """{"thumbprint":"aabb","notBefore":"2026-01-01T00:00:00+00:00","notAfter":"2028-01-01T00:00:00+00:00","isActive":true,"keyVaultSecretName":"boot-media-cert-aabb","previous":null}""";
        var core = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(active, Encoding.UTF8, "application/json") });
        var context = FunctionHttp.Context();

        var response = await Create(core).GetCertificateMetadata(FunctionHttp.Request(context, "GET", "https://operator/api/bootmedia/certificate/metadata"), context);

        using var json = JsonDocument.Parse(await response.ReadBodyAsync());
        json.RootElement.GetProperty("previous").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound, HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable)]
    public async Task StopAcceptingPrevious_ForwardsToImagingCore(HttpStatusCode coreStatus, HttpStatusCode expected)
    {
        var core = new CapturingHandler(_ => new HttpResponseMessage(coreStatus));
        var context = FunctionHttp.Context();

        var response = await Create(core).StopAcceptingPrevious(FunctionHttp.Request(context, "POST", "https://operator/api/cert/previous/stop"), context);

        response.StatusCode.Should().Be(expected);
        core.Requests.Single().Request.RequestUri!.AbsolutePath.Should().Be("/api/internal/cert/previous/stop");
    }

    [Fact]
    public void StopAcceptingPrevious_RequiresPortalAccess()
    {
        const string function = "StopAcceptingPreviousBootMediaCertificate";
        AppRoleAuthorizationMiddleware.Deny(new HashSet<string> { AppRoleAuthorizationMiddleware.PortalAccessRole }, function).Should().BeNull();
        AppRoleAuthorizationMiddleware.Deny(new HashSet<string> { AppRoleAuthorizationMiddleware.MediaBuilderAccessRole }, function).Should().NotBeNull();
    }
}
