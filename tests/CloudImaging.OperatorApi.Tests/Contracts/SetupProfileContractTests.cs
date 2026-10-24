using System.Net;
using System.Text;
using System.Text.Json;
using CloudImaging.OperatorApi.Functions;
using CloudImaging.OperatorApi.Middleware;
using CloudImaging.OperatorApi.Services;
using CloudImaging.OperatorApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>
/// The Operator API forwards setup profile calls to Imaging Core unchanged and relays Core's
/// status and body, so the portal sees Core's problems and conflicts as they were written.
/// </summary>
public sealed class SetupProfileContractTests
{
    private static readonly Guid ProfileId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();
    private const string Body = """{"name":"Stockholm and Gothenburg","targeting":{"allDevices":false,"locationIds":["1b0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10"]},"actor":{"upn":"ops@contoso.com"}}""";
    private const string Problem = """{"type":"about:blank","title":"Validation failed","status":400,"detail":"Fix the problems and save again.","errors":[{"code":"plainTextPassword","line":42,"message":"A password is stored in plain text."}]}""";

    private delegate Task<HttpResponseData> Call(SetupProfileFunctions f, HttpRequestData req, FunctionContext ctx);

    public static TheoryData<string, string, string, string, string> Routes => new()
    {
        { "GetSetupProfiles", "GET", "setup-profiles", "/api/internal/setup-profiles", "" },
        { "GetSetupProfile", "GET", $"setup-profiles/{ProfileId}", $"/api/internal/setup-profiles/{ProfileId}", "" },
        { "CreateSetupProfile", "POST", "setup-profiles", "/api/internal/setup-profiles", Body },
        { "UpdateSetupProfile", "PUT", $"setup-profiles/{ProfileId}", $"/api/internal/setup-profiles/{ProfileId}", Body },
        { "DeleteSetupProfile", "DELETE", $"setup-profiles/{ProfileId}?changedBy=ops%40contoso.com", $"/api/internal/setup-profiles/{ProfileId}?changedBy=ops%40contoso.com", "" },
        { "RestoreSetupProfileVersion", "POST", $"setup-profiles/{ProfileId}/restore", $"/api/internal/setup-profiles/{ProfileId}/restore", """{"version":3,"baseVersion":8,"actor":{"upn":"ops@contoso.com"}}""" },
        { "GetSetupProfileVersions", "GET", $"setup-profiles/{ProfileId}/versions", $"/api/internal/setup-profiles/{ProfileId}/versions", "" },
        { "GetSetupProfileVersion", "GET", $"setup-profiles/{ProfileId}/versions/3", $"/api/internal/setup-profiles/{ProfileId}/versions/3", "" },
        { "GetSetupProfileHistory", "GET", $"setup-profiles/{ProfileId}/history", $"/api/internal/setup-profiles/{ProfileId}/history", "" },
    };

    private static readonly Dictionary<string, Call> Calls = new()
    {
        ["GetSetupProfiles"] = (f, r, c) => f.GetSetupProfiles(r, c),
        ["GetSetupProfile"] = (f, r, c) => f.GetSetupProfile(r, ProfileId.ToString(), c),
        ["CreateSetupProfile"] = (f, r, c) => f.CreateSetupProfile(r, c),
        ["UpdateSetupProfile"] = (f, r, c) => f.UpdateSetupProfile(r, ProfileId.ToString(), c),
        ["DeleteSetupProfile"] = (f, r, c) => f.DeleteSetupProfile(r, ProfileId.ToString(), c),
        ["RestoreSetupProfileVersion"] = (f, r, c) => f.RestoreSetupProfileVersion(r, ProfileId.ToString(), c),
        ["GetSetupProfileVersions"] = (f, r, c) => f.GetSetupProfileVersions(r, ProfileId.ToString(), c),
        ["GetSetupProfileVersion"] = (f, r, c) => f.GetSetupProfileVersion(r, ProfileId.ToString(), "3", c),
        ["GetSetupProfileHistory"] = (f, r, c) => f.GetSetupProfileHistory(r, ProfileId.ToString(), c),
    };

    private static ImagingCoreClient Core(CapturingHandler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://core.internal") });

    [Theory]
    [InlineData("script")]
    [InlineData("answerFile")]
    [InlineData("unknown")]
    public async Task ContentUpload_StreamsTheRawBodyAndKind_WithoutParsing(string kind)
    {
        const string text = "Write-Output 'H\u00e4lsningar' { not json";
        const string coreBody = """{"sha256":"aa","sizeBytes":31}""";
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.OK, coreBody));
        var functions = new SetupProfileFunctions(Core(handler), NullLogger<SetupProfileFunctions>.Instance);
        var context = FunctionHttp.Context();
        var request = FunctionHttp.Request(context, "POST", $"https://operator/api/setup-profiles/content?kind={kind}", text);
        request.Headers.Add("Content-Type", "application/octet-stream");

        var response = await functions.UploadSetupProfileContent(request, context);

        var (sent, body) = handler.Requests.Single();
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.PathAndQuery.Should().Be($"/api/internal/setup-profiles/content?kind={kind}");
        sent.Content!.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        body.Should().Be(text);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.ReadBodyAsync()).Should().Be(coreBody);
    }

    [Fact]
    public async Task ContentUpload_PassesTheUploadingUserOn()
    {
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.OK, """{"sha256":"aa","sizeBytes":1}"""));
        var functions = new SetupProfileFunctions(Core(handler), NullLogger<SetupProfileFunctions>.Instance);
        var context = FunctionHttp.Context();

        await functions.UploadSetupProfileContent(FunctionHttp.Request(context, "POST", "https://operator/api/setup-profiles/content?kind=script&uploadedBy=olle%40contoso.com", "x"), context);

        handler.Requests.Single().Item1.RequestUri!.PathAndQuery.Should().Be("/api/internal/setup-profiles/content?kind=script&uploadedBy=olle%40contoso.com");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task ContentUpload_RelaysCoreRefusals(HttpStatusCode status)
    {
        const string problem = """{"status":413,"detail":"This script is 25.3 MB. A script can be at most 24 MB."}""";
        var handler = new CapturingHandler(_ => Respond(status, problem, "application/problem+json"));
        var functions = new SetupProfileFunctions(Core(handler), NullLogger<SetupProfileFunctions>.Instance);
        var context = FunctionHttp.Context();

        var response = await functions.UploadSetupProfileContent(FunctionHttp.Request(context, "POST", "https://operator/api/setup-profiles/content?kind=script", "x"), context);

        response.StatusCode.Should().Be(status);
        response.ContentType().Should().Be("application/problem+json");
        (await response.ReadBodyAsync()).Should().Be(problem);
    }

    [Fact]
    public async Task ContentRead_RelaysTheTextAndItsMediaType()
    {
        var sha = new string('a', 64);
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.OK, "Write-Output 'H\u00e4j'", "text/plain"));
        var functions = new SetupProfileFunctions(Core(handler), NullLogger<SetupProfileFunctions>.Instance);
        var context = FunctionHttp.Context();

        var response = await functions.GetSetupProfileContent(FunctionHttp.Request(context, "GET", "https://operator/api/x"), ProfileId.ToString(), sha, context);

        handler.Requests.Single().Request.RequestUri!.AbsolutePath.Should().Be($"/api/internal/setup-profiles/{ProfileId}/content/{sha}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.ContentType().Should().Be("text/plain; charset=utf-8");
        (await response.ReadBodyAsync()).Should().Be("Write-Output 'H\u00e4j'");
    }

    [Fact]
    public async Task ContentRead_NotFound_IsRelayed_AndANonGuidIdIs400()
    {
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.NotFound));
        var functions = new SetupProfileFunctions(Core(handler), NullLogger<SetupProfileFunctions>.Instance);
        var context = FunctionHttp.Context();

        (await functions.GetSetupProfileContent(FunctionHttp.Request(context, "GET", "https://operator/api/x"), ProfileId.ToString(), "abc", context)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await functions.GetSetupProfileContent(FunctionHttp.Request(context, "GET", "https://operator/api/x"), "x", "abc", context)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        handler.Requests.Should().ContainSingle();
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string? body = null, string mediaType = "application/json") =>
        body is null ? new HttpResponseMessage(status) : new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static async Task<(HttpResponseData Response, CapturingHandler Handler)> RunAsync(string function, string method, string route, string body, HttpResponseMessage core)
    {
        var handler = new CapturingHandler(_ => core);
        var functions = new SetupProfileFunctions(Core(handler), NullLogger<SetupProfileFunctions>.Instance);
        var context = FunctionHttp.Context();
        var response = await Calls[function](functions, FunctionHttp.Request(context, method, $"https://operator/api/{route}", body), context);
        return (response, handler);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EachFunction_ForwardsMethodRouteAndBodyUnchanged(string function, string method, string route, string corePath, string body)
    {
        var (_, handler) = await RunAsync(function, method, route, body, Respond(HttpStatusCode.OK, "{}"));

        var (request, sent) = handler.Requests.Single();
        request.Method.Method.Should().Be(method);
        request.RequestUri!.PathAndQuery.Should().Be(corePath);
        if (body.Length > 0)
        {
            using var expected = JsonDocument.Parse(body);
            using var actual = JsonDocument.Parse(sent!);
            actual.RootElement.GetRawText().Should().Be(expected.RootElement.GetRawText());
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Status_AndBody_AreRelayed(HttpStatusCode status)
    {
        const string coreBody = """{"detail":"Saved by ops@contoso.com at 2026-10-23 10:15."}""";

        var (response, _) = await RunAsync("UpdateSetupProfile", "PUT", $"setup-profiles/{ProfileId}", Body, Respond(status, coreBody));

        response.StatusCode.Should().Be(status);
        (await response.ReadBodyAsync()).Should().Be(coreBody);
    }

    [Fact]
    public async Task NoContent_IsRelayedWithoutABody()
    {
        var (response, _) = await RunAsync("DeleteSetupProfile", "DELETE", $"setup-profiles/{ProfileId}", "", Respond(HttpStatusCode.NoContent));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await response.ReadBodyAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ValidationProblem_KeepsErrorsAndMediaType()
    {
        var (response, _) = await RunAsync("CreateSetupProfile", "POST", "setup-profiles", Body, Respond(HttpStatusCode.BadRequest, Problem, "application/problem+json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.ContentType().Should().Be("application/problem+json");
        using var json = JsonDocument.Parse(await response.ReadBodyAsync());
        json.RootElement.GetProperty("errors")[0].GetProperty("line").GetInt32().Should().Be(42);
    }

    [Theory]
    [InlineData("GetSetupProfile")]
    [InlineData("UpdateSetupProfile")]
    [InlineData("DeleteSetupProfile")]
    public async Task NonGuidId_Returns400_WithoutCallingCore(string function)
    {
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.OK));
        var functions = new SetupProfileFunctions(Core(handler), NullLogger<SetupProfileFunctions>.Instance);
        var context = FunctionHttp.Context();
        var request = FunctionHttp.Request(context, "PUT", "https://operator/api/setup-profiles/x", Body);

        var response = function switch
        {
            "GetSetupProfile" => await functions.GetSetupProfile(request, "x", context),
            "UpdateSetupProfile" => await functions.UpdateSetupProfile(request, "x", context),
            _ => await functions.DeleteSetupProfile(request, "x", context),
        };

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("GetSetupProfiles")]
    [InlineData("GetSetupProfile")]
    [InlineData("CreateSetupProfile")]
    [InlineData("UpdateSetupProfile")]
    [InlineData("DeleteSetupProfile")]
    [InlineData("RestoreSetupProfileVersion")]
    [InlineData("GetSetupProfileVersions")]
    [InlineData("GetSetupProfileVersion")]
    [InlineData("GetSetupProfileHistory")]
    [InlineData("GetSessionStepItems")]
    [InlineData("UploadSetupProfileContent")]
    [InlineData("GetSetupProfileContent")]
    public void MediaBuilderAccessAlone_IsRefused(string function)
    {
        AppRoleAuthorizationMiddleware.Deny(new HashSet<string> { AppRoleAuthorizationMiddleware.PortalAccessRole }, function).Should().BeNull();
        AppRoleAuthorizationMiddleware.Deny(new HashSet<string> { AppRoleAuthorizationMiddleware.MediaBuilderAccessRole }, function).Should().NotBeNull();
    }

    [Fact]
    public async Task SessionStepItems_ForwardsToCore_AndRelaysTheList()
    {
        const string items = """[{"index":0,"kind":"WinPEScript","name":"Enable Wake on LAN","outcome":"Failed"}]""";
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.OK, items));
        var functions = new SessionQueryFunctions(Core(handler), NullLogger<SessionQueryFunctions>.Instance);
        var context = FunctionHttp.Context();

        var response = await functions.GetSessionStepItems(FunctionHttp.Request(context, "GET", "https://operator/api/x"), SessionId.ToString(), "PrepareSetup", context);

        handler.Requests.Single().Request.RequestUri!.AbsolutePath.Should().Be($"/api/internal/sessions/{SessionId}/steps/PrepareSetup/items");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.ReadBodyAsync()).Should().Be(items);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task SessionStepItems_RelaysCoreRefusals(HttpStatusCode status)
    {
        var handler = new CapturingHandler(_ => Respond(status));
        var functions = new SessionQueryFunctions(Core(handler), NullLogger<SessionQueryFunctions>.Instance);
        var context = FunctionHttp.Context();

        var response = await functions.GetSessionStepItems(FunctionHttp.Request(context, "GET", "https://operator/api/x"), SessionId.ToString(), "Unknown", context);

        response.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task DeleteLocation_ForwardsChangedBy_AndRelaysTheConflictDetail()
    {
        var locationId = Guid.NewGuid();
        const string conflict = """{"status":409,"detail":"Malmo Lab is the only target of Malmo Lab setup. Change that profile's targeting first."}""";
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.Conflict, conflict, "application/problem+json"));
        var functions = new LocationFunctions(Core(handler), NullLogger<LocationFunctions>.Instance);
        var context = FunctionHttp.Context();

        var response = await functions.DeleteLocation(FunctionHttp.Request(context, "DELETE", $"https://operator/api/locations/{locationId}?changedBy=ops%40contoso.com"), locationId.ToString(), context);

        handler.Requests.Single().Request.RequestUri!.PathAndQuery.Should().Be($"/api/internal/locations/{locationId}?changedBy=ops%40contoso.com");
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var json = JsonDocument.Parse(await response.ReadBodyAsync());
        json.RootElement.GetProperty("detail").GetString().Should().Contain("only target");
    }

    [Fact]
    public async Task PutConfiguration_ForwardsRetentionAndChangedBy()
    {
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.NoContent));
        var functions = new PortalConfigurationFunctions(Core(handler), NullLogger<PortalConfigurationFunctions>.Instance);
        var context = FunctionHttp.Context();
        const string body = """{"setupProfileVersionRetention":40,"changedBy":"admin@contoso.com"}""";

        var response = await functions.PutPortalConfiguration(FunctionHttp.Request(context, "PUT", "https://operator/api/configuration", body), context);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        sent.RootElement.GetProperty("setupProfileVersionRetention").GetInt32().Should().Be(40);
        sent.RootElement.GetProperty("changedBy").GetString().Should().Be("admin@contoso.com");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(101)]
    public async Task PutConfiguration_RefusesRetentionOutOfRange_WithoutCallingCore(int retention)
    {
        var handler = new CapturingHandler(_ => Respond(HttpStatusCode.NoContent));
        var functions = new PortalConfigurationFunctions(Core(handler), NullLogger<PortalConfigurationFunctions>.Instance);
        var context = FunctionHttp.Context();

        var response = await functions.PutPortalConfiguration(FunctionHttp.Request(context, "PUT", "https://operator/api/configuration", $$"""{"setupProfileVersionRetention":{{retention}}}"""), context);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadBodyAsync()).Should().Be("Keep between 5 and 100 versions per profile.");
        handler.Requests.Should().BeEmpty();
    }
}
