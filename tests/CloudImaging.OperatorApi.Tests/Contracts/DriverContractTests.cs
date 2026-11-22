using System.Net;
using System.Text;
using CloudImaging.OperatorApi.Functions;
using CloudImaging.OperatorApi.Services;
using CloudImaging.OperatorApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>
/// The Operator API forwards driver calls to Imaging Core unchanged (method, path, query, body)
/// and relays Core's status, body and Retry-After.
/// </summary>
public sealed class DriverContractTests
{
    private static readonly Guid ModelId = Guid.NewGuid();
    private static readonly Guid RuleId = Guid.NewGuid();
    private const string RuleBody = """{"name":"Skip","scope":"Hp","match":"Category","value":"Audio","action":"Skip","actor":{"upn":"ops@contoso.com"}}""";
    private const string ActorBody = """{"actor":{"upn":"ops@contoso.com","objectId":"oid"}}""";
    private const string OnboardBody = """{"vendor":"Hp","catalogModelKeys":["8C26_X64"],"releases":["Windows11_24H2"],"actor":{"upn":"ops@contoso.com"}}""";

    private delegate Task<HttpResponseData> Call(DriverFunctions f, HttpRequestData req, FunctionContext ctx);

    public static TheoryData<string, string, string, string, string> Routes => new()
    {
        { "GetDriverVendors", "GET", "drivers/vendors", "/api/internal/drivers/vendors", "" },
        { "GetDriverCatalogModels", "GET", "drivers/catalog/models?vendor=Hp&search=840", "/api/internal/drivers/catalog/models?vendor=Hp&search=840", "" },
        { "PreviewDriverModels", "POST", "drivers/models/preview", "/api/internal/drivers/models/preview", OnboardBody },
        { "OnboardDriverModels", "POST", "drivers/models", "/api/internal/drivers/models", OnboardBody },
        { "GetDriverModels", "GET", "drivers/models", "/api/internal/drivers/models", "" },
        { "GetDriverModel", "GET", $"drivers/models/{ModelId}?release=Windows11_24H2", $"/api/internal/drivers/models/{ModelId}?release=Windows11_24H2", "" },
        { "CompleteDriverModelOnboarding", "POST", $"drivers/models/{ModelId}/complete", $"/api/internal/drivers/models/{ModelId}/complete", ActorBody },
        { "AddDriverModelRelease", "POST", $"drivers/models/{ModelId}/releases", $"/api/internal/drivers/models/{ModelId}/releases", """{"release":"Windows11_25H2","actor":{"upn":"ops@contoso.com"}}""" },
        { "StartDriverSyncRun", "POST", "drivers/sync/runs", "/api/internal/drivers/sync/runs", ActorBody },
        { "GetDriverSyncRuns", "GET", "drivers/sync/runs", "/api/internal/drivers/sync/runs", "" },
        { "CheckAllDriverModelUpdates", "POST", "drivers/models/check", "/api/internal/drivers/models/check", ActorBody },
        { "CheckDriverModelUpdates", "POST", $"drivers/models/{ModelId}/check", $"/api/internal/drivers/models/{ModelId}/check", ActorBody },
        { "GetDriverRules", "GET", "drivers/rules", "/api/internal/drivers/rules", "" },
        { "GetDriverRuleSuggestions", "GET", "drivers/rules/suggestions", "/api/internal/drivers/rules/suggestions", "" },
        { "CreateDriverRule", "POST", "drivers/rules", "/api/internal/drivers/rules", RuleBody },
        { "UpdateDriverRule", "PUT", $"drivers/rules/{RuleId}", $"/api/internal/drivers/rules/{RuleId}", RuleBody },
        { "DeleteDriverRule", "DELETE", $"drivers/rules/{RuleId}?changedBy=ops%40contoso.com", $"/api/internal/drivers/rules/{RuleId}?changedBy=ops%40contoso.com", "" },
        { "GetDriverCategories", "GET", "drivers/categories?vendor=Hp", "/api/internal/drivers/categories?vendor=Hp", "" },
        { "SetDriverCriticalCategories", "PUT", "drivers/critical-categories/Hp", "/api/internal/drivers/critical-categories/Hp", """{"categories":["Driver - Network"],"actor":{"upn":"ops@contoso.com"}}""" },
        { "SetDriverOverride", "PUT", $"drivers/models/{ModelId}/releases/Windows11_24H2/overrides/sp100", $"/api/internal/drivers/models/{ModelId}/releases/Windows11_24H2/overrides/sp100", """{"action":"Skip","actor":{"upn":"ops@contoso.com"}}""" },
        { "ClearDriverOverride", "DELETE", $"drivers/models/{ModelId}/releases/Windows11_24H2/overrides/sp100?changedBy=ops%40contoso.com", $"/api/internal/drivers/models/{ModelId}/releases/Windows11_24H2/overrides/sp100?changedBy=ops%40contoso.com", "" },
        { "BuildDriverSetNow", "POST", $"drivers/models/{ModelId}/releases/Windows11_24H2/build", $"/api/internal/drivers/models/{ModelId}/releases/Windows11_24H2/build", ActorBody },
        { "SetDriverVendor", "PUT", "drivers/vendors/Hp", "/api/internal/drivers/vendors/Hp", """{"syncEnabled":true,"actor":{"upn":"ops@contoso.com"}}""" },
        { "GetDriverSyncSettings", "GET", "drivers/sync/settings", "/api/internal/drivers/sync/settings", "" },
        { "SetDriverSyncSettings", "PUT", "drivers/sync/settings", "/api/internal/drivers/sync/settings", """{"schedule":"Daily","localTime":"02:00","timeZone":"Europe/Berlin","actor":{"upn":"ops@contoso.com"}}""" },
        { "GetDriverSyncRunItems", "GET", $"drivers/sync/runs/{RuleId}/items", $"/api/internal/drivers/sync/runs/{RuleId}/items", "" },
        { "GetDriverSyncRunProgress", "GET", $"drivers/sync/runs/{RuleId}/progress", $"/api/internal/drivers/sync/runs/{RuleId}/progress", "" },
        { "GetDriverSyncReport", "GET", "drivers/reports/sync?from=2026-10-01T00%3A00%3A00Z&to=2026-10-26T23%3A59%3A59Z", "/api/internal/drivers/reports/sync?from=2026-10-01T00%3A00%3A00Z&to=2026-10-26T23%3A59%3A59Z", "" },
        { "ApproveDriverComponents", "POST", $"drivers/models/{ModelId}/releases/Windows11_24H2/approvals", $"/api/internal/drivers/models/{ModelId}/releases/Windows11_24H2/approvals", """{"lineageKeys":["sp500"],"actor":{"upn":"ops@contoso.com"}}""" },
        { "RollBackDriverSet", "POST", $"drivers/models/{ModelId}/releases/Windows11_24H2/rollback", $"/api/internal/drivers/models/{ModelId}/releases/Windows11_24H2/rollback", ActorBody },
        { "ReleaseDriverSetInTesting", "POST", $"drivers/models/{ModelId}/releases/Windows11_24H2/testing/release", $"/api/internal/drivers/models/{ModelId}/releases/Windows11_24H2/testing/release", ActorBody },
        { "GetDriverRemovalImpact", "GET", $"drivers/models/{ModelId}/removal-impact?release=Windows11_24H2", $"/api/internal/drivers/models/{ModelId}/removal-impact?release=Windows11_24H2", "" },
        { "RemoveDriverModel", "DELETE", $"drivers/models/{ModelId}?changedBy=ops%40contoso.com", $"/api/internal/drivers/models/{ModelId}?changedBy=ops%40contoso.com", "" },
        { "RemoveDriverModelRelease", "DELETE", $"drivers/models/{ModelId}/releases/Windows11_24H2?changedBy=ops%40contoso.com", $"/api/internal/drivers/models/{ModelId}/releases/Windows11_24H2?changedBy=ops%40contoso.com", "" },
        { "RemoveWithdrawnDriverComponent", "DELETE", $"drivers/models/{ModelId}/releases/Windows11_24H2/components/sp100?changedBy=ops%40contoso.com", $"/api/internal/drivers/models/{ModelId}/releases/Windows11_24H2/components/sp100?changedBy=ops%40contoso.com", "" },
        { "GetDriverStorage", "GET", "drivers/storage", "/api/internal/drivers/storage", "" },
        { "SetDriverStorageSettings", "PUT", "drivers/storage/settings", "/api/internal/drivers/storage/settings", """{"previousVersionsToKeep":2,"actor":{"upn":"ops@contoso.com"}}""" },
        { "GetDriverStorageModel", "GET", $"drivers/storage/models/{ModelId}", $"/api/internal/drivers/storage/models/{ModelId}", "" },
        { "GetDriverDownloads", "GET", "drivers/downloads", "/api/internal/drivers/downloads", "" },
        { "PreviewDriverAssignment", "POST", "drivers/assignment-preview", "/api/internal/drivers/assignment-preview", $"{{\"osImageId\":\"{ModelId}\",\"sessionIds\":[\"{RuleId}\"]}}" },
        { "GetDriverComponentVersions", "GET", $"drivers/models/{ModelId}/releases/Windows11_24H2/components/sp100/versions?check=true", $"/api/internal/drivers/models/{ModelId}/releases/Windows11_24H2/components/sp100/versions?check=true", "" },
        { "SetDriverComponentRollBack", "PUT", $"drivers/models/{ModelId}/releases/Windows11_24H2/components/sp100/rollback", $"/api/internal/drivers/models/{ModelId}/releases/Windows11_24H2/components/sp100/rollback", """{"version":"1.0","until":"NewerVersion","actor":{"upn":"ops@contoso.com"}}""" },
        { "ClearDriverComponentRollBack", "DELETE", $"drivers/models/{ModelId}/releases/Windows11_24H2/components/sp100/rollback?changedBy=ops%40contoso.com", $"/api/internal/drivers/models/{ModelId}/releases/Windows11_24H2/components/sp100/rollback?changedBy=ops%40contoso.com", "" },
        { "StartCustomComponentUpload", "POST", "drivers/custom-components/uploads/start", "/api/internal/drivers/custom-components/uploads/start", """{"fileName":"intel.zip","sizeBytes":1024}""" },
        { "CompleteCustomComponentUpload", "POST", $"drivers/custom-components/uploads/{RuleId}/complete", $"/api/internal/drivers/custom-components/uploads/{RuleId}/complete", """{"blockIds":["AA=="]}""" },
        { "AbandonCustomComponentUpload", "POST", $"drivers/custom-components/uploads/{RuleId}/abandon", $"/api/internal/drivers/custom-components/uploads/{RuleId}/abandon", "" },
        { "CreateCustomComponent", "POST", "drivers/custom-components", "/api/internal/drivers/custom-components", """{"name":"Intel","actor":{"upn":"ops@contoso.com"}}""" },
        { "GetCustomComponents", "GET", "drivers/custom-components", "/api/internal/drivers/custom-components", "" },
        { "UpdateCustomComponent", "PUT", $"drivers/custom-components/{RuleId}", $"/api/internal/drivers/custom-components/{RuleId}", """{"name":"Intel","actor":{"upn":"ops@contoso.com"}}""" },
        { "DeleteCustomComponent", "DELETE", $"drivers/custom-components/{RuleId}?changedBy=ops%40contoso.com", $"/api/internal/drivers/custom-components/{RuleId}?changedBy=ops%40contoso.com", "" },
        { "DismissCustomComponentNotice", "POST", $"drivers/custom-components/{RuleId}/notices/{ModelId}/dismiss", $"/api/internal/drivers/custom-components/{RuleId}/notices/{ModelId}/dismiss", ActorBody },
        { "GetDriverSetsForMediaBuilder", "GET", "media-builder/driver-sets?architecture=X64", "/api/internal/drivers/media-builder/sets?architecture=X64", "" },
        { "GetDriverSetLinksForMediaBuilder", "POST", "media-builder/driver-sets/links", "/api/internal/drivers/media-builder/links", $"{{\"sets\":[{{\"modelId\":\"{ModelId}\",\"release\":\"Windows11_24H2\"}}]}}" },
    };

    private static readonly Dictionary<string, Call> Calls = new()
    {
        ["GetDriverVendors"] = (f, r, c) => f.GetDriverVendors(r, c),
        ["GetDriverCatalogModels"] = (f, r, c) => f.GetDriverCatalogModels(r, c),
        ["PreviewDriverModels"] = (f, r, c) => f.PreviewDriverModels(r, c),
        ["OnboardDriverModels"] = (f, r, c) => f.OnboardDriverModels(r, c),
        ["GetDriverModels"] = (f, r, c) => f.GetDriverModels(r, c),
        ["GetDriverModel"] = (f, r, c) => f.GetDriverModel(r, ModelId.ToString(), c),
        ["CompleteDriverModelOnboarding"] = (f, r, c) => f.CompleteDriverModelOnboarding(r, ModelId.ToString(), c),
        ["AddDriverModelRelease"] = (f, r, c) => f.AddDriverModelRelease(r, ModelId.ToString(), c),
        ["StartDriverSyncRun"] = (f, r, c) => f.StartDriverSyncRun(r, c),
        ["GetDriverSyncRuns"] = (f, r, c) => f.GetDriverSyncRuns(r, c),
        ["CheckAllDriverModelUpdates"] = (f, r, c) => f.CheckAllDriverModelUpdates(r, c),
        ["CheckDriverModelUpdates"] = (f, r, c) => f.CheckDriverModelUpdates(r, ModelId.ToString(), c),
        ["GetDriverRules"] = (f, r, c) => f.GetDriverRules(r, c),
        ["GetDriverRuleSuggestions"] = (f, r, c) => f.GetDriverRuleSuggestions(r, c),
        ["CreateDriverRule"] = (f, r, c) => f.CreateDriverRule(r, c),
        ["UpdateDriverRule"] = (f, r, c) => f.UpdateDriverRule(r, RuleId.ToString(), c),
        ["DeleteDriverRule"] = (f, r, c) => f.DeleteDriverRule(r, RuleId.ToString(), c),
        ["GetDriverCategories"] = (f, r, c) => f.GetDriverCategories(r, c),
        ["SetDriverCriticalCategories"] = (f, r, c) => f.SetDriverCriticalCategories(r, "Hp", c),
        ["SetDriverOverride"] = (f, r, c) => f.SetDriverOverride(r, ModelId.ToString(), "Windows11_24H2", "sp100", c),
        ["ClearDriverOverride"] = (f, r, c) => f.ClearDriverOverride(r, ModelId.ToString(), "Windows11_24H2", "sp100", c),
        ["BuildDriverSetNow"] = (f, r, c) => f.BuildDriverSetNow(r, ModelId.ToString(), "Windows11_24H2", c),
        ["SetDriverVendor"] = (f, r, c) => f.SetDriverVendor(r, "Hp", c),
        ["GetDriverSyncSettings"] = (f, r, c) => f.GetDriverSyncSettings(r, c),
        ["SetDriverSyncSettings"] = (f, r, c) => f.SetDriverSyncSettings(r, c),
        ["GetDriverSyncRunItems"] = (f, r, c) => f.GetDriverSyncRunItems(r, RuleId.ToString(), c),
        ["GetDriverSyncRunProgress"] = (f, r, c) => f.GetDriverSyncRunProgress(r, RuleId.ToString(), c),
        ["GetDriverSyncReport"] = (f, r, c) => f.GetDriverSyncReport(r, c),
        ["ApproveDriverComponents"] = (f, r, c) => f.ApproveDriverComponents(r, ModelId.ToString(), "Windows11_24H2", c),
        ["RollBackDriverSet"] = (f, r, c) => f.RollBackDriverSet(r, ModelId.ToString(), "Windows11_24H2", c),
        ["ReleaseDriverSetInTesting"] = (f, r, c) => f.ReleaseDriverSetInTesting(r, ModelId.ToString(), "Windows11_24H2", c),
        ["GetDriverRemovalImpact"] = (f, r, c) => f.GetDriverRemovalImpact(r, ModelId.ToString(), c),
        ["RemoveDriverModel"] = (f, r, c) => f.RemoveDriverModel(r, ModelId.ToString(), c),
        ["RemoveDriverModelRelease"] = (f, r, c) => f.RemoveDriverModelRelease(r, ModelId.ToString(), "Windows11_24H2", c),
        ["RemoveWithdrawnDriverComponent"] = (f, r, c) => f.RemoveWithdrawnDriverComponent(r, ModelId.ToString(), "Windows11_24H2", "sp100", c),
        ["GetDriverStorage"] = (f, r, c) => f.GetDriverStorage(r, c),
        ["SetDriverStorageSettings"] = (f, r, c) => f.SetDriverStorageSettings(r, c),
        ["GetDriverStorageModel"] = (f, r, c) => f.GetDriverStorageModel(r, ModelId.ToString(), c),
        ["GetDriverDownloads"] = (f, r, c) => f.GetDriverDownloads(r, c),
        ["PreviewDriverAssignment"] = (f, r, c) => f.PreviewDriverAssignment(r, c),
        ["GetDriverComponentVersions"] = (f, r, c) => f.GetDriverComponentVersions(r, ModelId.ToString(), "Windows11_24H2", "sp100", c),
        ["SetDriverComponentRollBack"] = (f, r, c) => f.SetDriverComponentRollBack(r, ModelId.ToString(), "Windows11_24H2", "sp100", c),
        ["ClearDriverComponentRollBack"] = (f, r, c) => f.ClearDriverComponentRollBack(r, ModelId.ToString(), "Windows11_24H2", "sp100", c),
        ["StartCustomComponentUpload"] = (f, r, c) => f.StartCustomComponentUpload(r, c),
        ["CompleteCustomComponentUpload"] = (f, r, c) => f.CompleteCustomComponentUpload(r, RuleId.ToString(), c),
        ["AbandonCustomComponentUpload"] = (f, r, c) => f.AbandonCustomComponentUpload(r, RuleId.ToString(), c),
        ["CreateCustomComponent"] = (f, r, c) => f.CreateCustomComponent(r, c),
        ["GetCustomComponents"] = (f, r, c) => f.GetCustomComponents(r, c),
        ["UpdateCustomComponent"] = (f, r, c) => f.UpdateCustomComponent(r, RuleId.ToString(), c),
        ["DeleteCustomComponent"] = (f, r, c) => f.DeleteCustomComponent(r, RuleId.ToString(), c),
        ["DismissCustomComponentNotice"] = (f, r, c) => f.DismissCustomComponentNotice(r, RuleId.ToString(), ModelId.ToString(), c),
        ["GetDriverSetsForMediaBuilder"] = (f, r, c) => f.GetDriverSetsForMediaBuilder(r, c),
        ["GetDriverSetLinksForMediaBuilder"] = (f, r, c) => f.GetDriverSetLinksForMediaBuilder(r, c),
    };

    private static DriverFunctions Functions(CapturingHandler handler) =>
        new(new ImagingCoreClient(new HttpClient(handler) { BaseAddress = new Uri("https://core.internal") }));

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Route_ForwardsMethodPathQueryAndBody_AndRelaysStatusAndBody(string name, string method, string path, string corePath, string body)
    {
        const string coreBody = """{"detail":"from core"}""";
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(coreBody, Encoding.UTF8, "application/problem+json") });
        var context = FunctionHttp.Context();

        var response = await Calls[name](Functions(handler), FunctionHttp.Request(context, method, $"https://operator/api/{path}", body), context);

        var (sent, sentBody) = handler.Requests.Single();
        sent.Method.Method.Should().Be(method);
        sent.RequestUri!.PathAndQuery.Should().Be(corePath);
        (sentBody ?? string.Empty).Should().Be(body);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.ContentType().Should().Be("application/problem+json");
        (await response.ReadBodyAsync()).Should().Be(coreBody);
    }

    [Theory]
    [InlineData("StartDriverSyncRun", "drivers/sync/runs")]
    [InlineData("CheckAllDriverModelUpdates", "drivers/models/check")]
    [InlineData("CheckDriverModelUpdates", "drivers/models/x/check")]
    public async Task TooSoon_RelaysRetryAfter(string name, string path)
    {
        var handler = new CapturingHandler(_ =>
        {
            var tooSoon = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("""{"availableAt":"2026-10-25T10:00:00Z"}""", Encoding.UTF8, "application/problem+json") };
            tooSoon.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1800));
            return tooSoon;
        });
        var context = FunctionHttp.Context();

        var response = await Calls[name](Functions(handler), FunctionHttp.Request(context, "POST", $"https://operator/api/{path}", ActorBody), context);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.GetValues("Retry-After").Single().Should().Be("1800");
        (await response.ReadBodyAsync()).Should().Contain("availableAt");
    }

    [Fact]
    public async Task NonGuidModelId_Is400_WithoutCallingCore()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var context = FunctionHttp.Context();

        var response = await Functions(handler).GetDriverModel(FunctionHttp.Request(context, "GET", "https://operator/api/drivers/models/x"), "x", context);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("x", "Windows11_24H2", "sp100")]
    [InlineData("00000000-0000-0000-0000-000000000001", "Windows 11", "sp100")]
    [InlineData("00000000-0000-0000-0000-000000000001", "Windows11_24H2", "sp/100")]
    public async Task Override_InvalidSegment_Is400_WithoutCallingCore(string id, string release, string key)
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var context = FunctionHttp.Context();

        var response = await Functions(handler).SetDriverOverride(FunctionHttp.Request(context, "PUT", "https://operator/api/drivers/models/x/releases/x/overrides/x", "{}"), id, release, key, context);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        handler.Requests.Should().BeEmpty();
    }
}
