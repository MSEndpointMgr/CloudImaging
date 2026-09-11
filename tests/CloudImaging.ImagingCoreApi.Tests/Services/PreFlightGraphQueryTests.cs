using System.Net;
using Azure.Core;
using CloudImaging.ImagingCoreApi.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services;

public sealed class PreFlightGraphQueryTests
{
    [Fact]
    public void AutopilotFilter_UsesExactEscapedSerialNumber()
    {
        DevicePreFlightAuthorizationService.BuildAutopilotSerialFilter("  ABC'123  ")
            .Should().Be("serialNumber eq 'ABC''123'");
    }

    [Fact]
    public void CorporateIdentifierRequest_UsesManufacturerModelSerialTuple()
    {
        var requestUri = CorporateIdentifierGraphClient.BuildRequestUri(
            "Microsoft", "Surface Laptop 6", "ABC123");
        var decoded = Uri.UnescapeDataString(requestUri);

        decoded.Should().StartWith("beta/deviceManagement/importedDeviceIdentities?");
        decoded.Should().Contain("importedDeviceIdentityType eq 'manufacturerModelSerial'");
        decoded.Should().Contain("importedDeviceIdentifier eq 'Microsoft,Surface Laptop 6,ABC123'");
        decoded.Should().Contain("$top=1");
    }

    [Fact]
    public void CorporateIdentifierRequest_TrimsAndEscapesTupleValues()
    {
        var requestUri = CorporateIdentifierGraphClient.BuildRequestUri(
            " Contoso ", " Worker's Laptop ", " SN'42 ");

        Uri.UnescapeDataString(requestUri)
            .Should().Contain("importedDeviceIdentifier eq 'Contoso,Worker''s Laptop,SN''42'");
    }

    [Theory]
    [InlineData("{\"value\":[{\"id\":\"device-id\"}]}", true)]
    [InlineData("{\"value\":[]}", false)]
    public async Task CorporateIdentifierLookup_UsesBearerTokenAndReadsMatches(
        string responseJson,
        bool expected)
    {
        var handler = new RecordingHandler(responseJson);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://graph.microsoft.com/"),
        };
        var client = new CorporateIdentifierGraphClient(httpClient, new StubTokenCredential());

        var exists = await client.ExistsAsync("Microsoft", "Surface Laptop 6", "ABC123", CancellationToken.None);

        exists.Should().Be(expected);
        handler.RequestUri.Should().NotBeNull();
        handler.RequestUri!.AbsolutePath.Should().Be("/beta/deviceManagement/importedDeviceIdentities");
        handler.AuthorizationScheme.Should().Be("Bearer");
        handler.AuthorizationParameter.Should().Be("test-token");
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        private static readonly AccessToken Token = new("test-token", DateTimeOffset.MaxValue);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => Token;

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) => ValueTask.FromResult(Token);
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson),
            });
        }
    }
}
