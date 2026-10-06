using System.Net;
using System.Text.Json;
using Azure.Core;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services;

public sealed class AutopilotGraphClientTests
{
    [Fact]
    public async Task Import_PostsTheImportedIdentity_AndReturnsItsId()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created, """{"id":"import-42"}""");
        var client = new AutopilotGraphClient(new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") }, new StubTokenCredential());

        var id = await client.ImportAsync("SN-1", "aGFzaA==", "EMEA-SE", CancellationToken.None);

        id.Should().Be("import-42");
        handler.Method.Should().Be(HttpMethod.Post);
        handler.Path.Should().Be("/v1.0/deviceManagement/importedWindowsAutopilotDeviceIdentities");
        handler.Authorization.Should().Be("Bearer test-token");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("serialNumber").GetString().Should().Be("SN-1");
        body.RootElement.GetProperty("hardwareIdentifier").GetString().Should().Be("aGFzaA==");
        body.RootElement.GetProperty("groupTag").GetString().Should().Be("EMEA-SE");
        body.RootElement.GetProperty("state").GetProperty("deviceImportStatus").GetString().Should().Be("pending");
    }

    /// <summary>Builds a hash in the 4K layout: header, then type/length/value records, then the checksum record.</summary>
    internal static string SyntheticHash(string? tpmVersion, byte[]? ekPub)
    {
        var records = new List<(ushort Type, byte[] Value)> { (1, new byte[24]), (14, "SN-1\0"u8.ToArray()) };
        if (tpmVersion is not null) records.Add((13, System.Text.Encoding.ASCII.GetBytes(tpmVersion + "\0")));
        if (ekPub is not null) records.Add((25, ekPub));
        records.Add((0x5343, new byte[32]));

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("HH"u8.ToArray());
        writer.Write((ushort)0);
        foreach (var (type, value) in records)
        {
            writer.Write(type);
            writer.Write((ushort)(value.Length + 4));
            writer.Write(value);
        }
        return Convert.ToBase64String(stream.ToArray());
    }

    [Fact]
    public void Inspect_FindsTpm20AndTheEndorsementKey()
    {
        var inspection = AutopilotHardwareHash.Inspect(SyntheticHash("2.0", [1, 2, 3, 4]));

        inspection.Should().Be(new AutopilotHashInspection(true, "2.0", true));
        inspection.PreProvisioningReady.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("2.0", false)]
    [InlineData("1.2", true)]
    public void Inspect_IsNotReady_WithoutTpm20Data(string? tpmVersion, bool withEkPub)
    {
        var inspection = AutopilotHardwareHash.Inspect(SyntheticHash(tpmVersion, withEkPub ? [1, 2, 3] : null));

        inspection.Decoded.Should().BeTrue();
        inspection.PreProvisioningReady.Should().BeFalse();
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("aGFzaA==")]
    [InlineData("")]
    public void Inspect_ReportsUndecoded_ForAnythingElse(string hash)
    {
        AutopilotHardwareHash.Inspect(hash).Decoded.Should().BeFalse();
    }

    [Fact]
    public async Task Forbidden_ExplainsTheMissingPermission()
    {
        var handler = new RecordingHandler(HttpStatusCode.Forbidden, """{"error":{"message":"Insufficient privileges"}}""");
        var client = new AutopilotGraphClient(new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") }, new StubTokenCredential());

        var act = () => client.ImportAsync("SN-1", "aGFzaA==", null, CancellationToken.None);

        (await act.Should().ThrowAsync<AutopilotGraphException>()).Which.Message.Should().Contain("DeviceManagementServiceConfig.ReadWrite.All");
    }

    [Fact]
    public async Task ImportStatus_ReadsStateAndError()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"state":{"deviceImportStatus":"error","deviceErrorCode":806,"deviceErrorName":"ZtdDeviceAlreadyAssigned"}}""");
        var client = new AutopilotGraphClient(new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") }, new StubTokenCredential());

        var status = await client.GetImportStatusAsync("import-42", CancellationToken.None);

        status.IsError.Should().BeTrue();
        status.ErrorCode.Should().Be("806");
        status.ErrorName.Should().Be("ZtdDeviceAlreadyAssigned");
    }

    [Theory]
    [InlineData("""{"value":[{"serialNumber":"SN-1"}]}""", true)]
    [InlineData("""{"value":[{"serialNumber":"SN-10"}]}""", false)]
    [InlineData("""{"value":[]}""", false)]
    public async Task SerialLookup_RequiresAnExactMatch(string json, bool expected)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, json);
        var client = new AutopilotGraphClient(new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") }, new StubTokenCredential());

        (await client.IsSerialRegisteredAsync("SN-1", CancellationToken.None)).Should().Be(expected);
        Uri.UnescapeDataString(handler.Query!).Should().Contain("contains(serialNumber,'SN-1')");
    }

    [Theory]
    [InlineData(AutopilotGroupTagKind.Static, "Kiosk", true)]
    [InlineData(AutopilotGroupTagKind.Static, "Bad/Tag", false)]
    [InlineData(AutopilotGroupTagKind.Template, "{Region}-{CountryCode}-STD", true)]
    [InlineData(AutopilotGroupTagKind.Template, "Plain text", false)]
    [InlineData(AutopilotGroupTagKind.Template, "{Department}-STD", false)]
    [InlineData(AutopilotGroupTagKind.Template, "{Region}/STD", false)]
    public void TemplateDefinitions_AreValidated(AutopilotGroupTagKind kind, string value, bool valid)
    {
        (AutopilotGroupTagTemplate.ValidateDefinition(kind, value) is null).Should().Be(valid);
    }

    [Fact]
    public void Template_ResolvesFromLocation_OrExplainsWhatIsMissing()
    {
        AutopilotGroupTagTemplate.Resolve("{Region}-{CountryCode}-STD", "Stockholm", "EMEA", "SE").Should().Be(("EMEA-SE-STD", (string?)null));

        var (value, reason) = AutopilotGroupTagTemplate.Resolve("{Region}-STD", "Stockholm", null, "SE");
        value.Should().BeNull();
        reason.Should().Contain("{Region}");

        AutopilotGroupTagTemplate.Resolve("{Region}-STD", null, null, null).UnavailableReason.Should().Be("The device has no location.");
    }

    [Fact]
    public void AutopilotChallenge_BindsTheHashAndDiffersFromTheSessionChallenge()
    {
        var a = DevicePayloadSignature.BuildAutopilotChallenge("SN", "aGFzaA==", "t", "n");
        var b = DevicePayloadSignature.BuildAutopilotChallenge("SN", "b3RoZXI=", "t", "n");

        a.Should().NotEqual(b);
        a.Should().NotEqual(DevicePayloadSignature.BuildChallenge("SN", "t", "n"));
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        private static readonly AccessToken Token = new("test-token", DateTimeOffset.MaxValue);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => Token;

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(Token);
    }

    private sealed class RecordingHandler(HttpStatusCode status, string responseJson) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Query { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Query = request.RequestUri.Query;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseJson) };
        }
    }
}
