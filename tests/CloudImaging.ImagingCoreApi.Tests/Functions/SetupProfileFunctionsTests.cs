using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.SetupProfiles;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static CloudImaging.ImagingCoreApi.Tests.TestDoubles.SetupProfileTestBed;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

public sealed class SetupProfileFunctionsTests
{
    private readonly SetupProfileTestBed _bed = new();
    private readonly SetupProfileFunctions _functions;

    public SetupProfileFunctionsTests() => _functions = new SetupProfileFunctions(_bed.Service);

    private static TestRequest Request(string method, object? body = null, string query = "") =>
        FunctionTestHost.Request(method, $"https://core/api/internal/setup-profiles{query}", body);

    private object CreateBody(string name = "Stockholm") => new
    {
        name,
        targeting = new { allDevices = false, locationIds = new[] { _bed.Stockholm } },
        content = new { failureHandling = "CompleteWithWarnings", registryValues = new[] { new { name = "Value", hive = "Software", key = "Policies\\Microsoft\\FVE", valueName = "X", type = "DWord", data = "7" } }, scripts = Array.Empty<object>(), answerFile = (object?)null },
        actor = new { upn = "ops@contoso.com", objectId = "oid" },
    };

    [Fact]
    public async Task Create_Returns201WithTheDetail()
    {
        var response = await _functions.CreateSetupProfile(Request("POST", CreateBody()), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.ReadJsonAsync();
        body.GetProperty("currentVersion").GetInt32().Should().Be(1);
        body.GetProperty("content").GetProperty("registryValues")[0].GetProperty("type").GetString().Should().Be("DWord");
    }

    [Fact]
    public async Task Create_InvalidBody_Returns400Problem()
    {
        var response = await _functions.CreateSetupProfile(Request("POST", "{ not json"), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("The request body is not a valid setup profile.");
    }

    [Fact]
    public async Task Create_RefusedAnswerFile_Returns400WithErrors()
    {
        var body = new
        {
            name = "Lab",
            targeting = new { allDevices = true, locationIds = Array.Empty<Guid>() },
            content = new
            {
                failureHandling = "CompleteWithWarnings",
                registryValues = Array.Empty<object>(),
                scripts = Array.Empty<object>(),
                answerFile = new { content = "<unattend xmlns=\"urn:schemas-microsoft-com:unattend\">\n<settings pass=\"windowsPE\"/>\n</unattend>" },
            },
            actor = new { upn = "ops@contoso.com" },
        };

        var response = await _functions.CreateSetupProfile(Request("POST", body), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.ReadJsonAsync();
        json.GetProperty("detail").GetString().Should().Be("The answer file has 1 problem.");
        json.GetProperty("errors")[0].GetProperty("code").GetString().Should().Be("windowsPePass");
        json.GetProperty("errors")[0].GetProperty("line").GetInt32().Should().Be(2);
        (await _bed.Service.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Get_UnknownOrDeleted_Returns404_AndNonGuidReturns400()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        await _bed.Service.DeleteAsync(detail.ProfileId, "ops@contoso.com");

        var deleted = await _functions.GetSetupProfile(Request("GET"), detail.ProfileId.ToString(), FunctionTestHost.Context);
        deleted.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await deleted.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("This setup profile no longer exists.");

        (await _functions.GetSetupProfile(Request("GET"), "not-a-guid", FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_ReturnsSummariesWithTargeting()
    {
        await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        await _bed.CreateAsync("Everywhere", AllDevices);

        var json = await (await _functions.GetSetupProfiles(Request("GET"), FunctionTestHost.Context)).ReadJsonAsync();

        json.GetArrayLength().Should().Be(2);
        json.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Everywhere").GetProperty("targeting").GetProperty("allDevices").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Update_StaleBaseVersion_Returns409()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        await _bed.SaveAsync(detail, Content(Value(data: "0")));

        var response = await _functions.UpdateSetupProfile(Request("PUT", new { baseVersion = 1, name = "Stockholm", targeting = new { locationIds = new[] { _bed.Stockholm } }, actor = new { upn = "ops@contoso.com" } }), detail.ProfileId.ToString(), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Delete_WithoutChangedBy_Returns400_AndWithIt204()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));

        (await _functions.DeleteSetupProfile(Request("DELETE"), detail.ProfileId.ToString(), FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _functions.DeleteSetupProfile(Request("DELETE", query: "?changedBy=ops%40contoso.com"), detail.ProfileId.ToString(), FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task StepItems_AreSortedFailedThenNotRunThenReported_AndUnknownStepIs400()
    {
        var session = await _bed.AddSessionAsync(SessionState.SessionInProgress);
        await _bed.Items.ReplaceAsync(session.SessionId, ImagingStepName.PrepareSetup,
        [
            new ImagingStepItem { Index = 0, Name = "ok", Outcome = StepItemOutcome.Succeeded },
            new ImagingStepItem { Index = 1, Name = "skipped", Outcome = StepItemOutcome.NotRun, Reason = "r" },
            new ImagingStepItem { Index = 2, Name = "broken", Outcome = StepItemOutcome.Failed, Reason = "r" },
        ]);
        var function = new SessionStepItemsFunction(_bed.Sessions, _bed.Items);

        var json = await (await function.Run(Request("GET"), session.SessionId.ToString(), "PrepareSetup", FunctionTestHost.Context)).ReadJsonAsync();
        json.EnumerateArray().Select(i => i.GetProperty("name").GetString()).Should().Equal("broken", "skipped", "ok");

        (await function.Run(Request("GET"), session.SessionId.ToString(), "Bogus", FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var empty = await function.Run(Request("GET"), session.SessionId.ToString(), "ApplyImage", FunctionTestHost.Context);
        (await empty.ReadJsonAsync()).GetArrayLength().Should().Be(0);
        (await function.Run(Request("GET"), Guid.NewGuid().ToString(), "PrepareSetup", FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SessionSummary_CarriesThePinAndStepFields()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        var session = await _bed.AddSessionAsync(SessionState.SessionInProgress, _bed.Stockholm, detail.ProfileId, 1);
        await _bed.Steps.UpsertAsync(session.SessionId, new ImagingStep { StepName = ImagingStepName.PrepareSetup, Status = ImagingStepStatus.Completed, WarningCount = 1, ItemCounts = [new ImagingStepItemCount { Kind = StepItemKind.WinPEScript, Outcome = StepItemOutcome.Failed, Count = 1 }] });
        var query = new SessionQueryFunctions(_bed.Sessions, _bed.Steps, new CloudImaging.ImagingCoreApi.Repositories.OsImageRepository(_bed.Tables), NullLogger<SessionQueryFunctions>.Instance);

        var json = await (await query.GetSessionById(Request("GET"), session.SessionId.ToString(), FunctionTestHost.Context)).ReadJsonAsync();

        json.GetProperty("setupProfile").GetProperty("version").GetInt32().Should().Be(1);
        var step = json.GetProperty("steps")[0];
        step.GetProperty("warningCount").GetInt32().Should().Be(1);
        step.GetProperty("itemCounts")[0].GetProperty("kind").GetString().Should().Be("WinPEScript");
        step.GetProperty("itemsTruncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Restore_CreatesTheNextVersion_AndAStaleBaseIs409_AndAnUnknownVersionIs404()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        await _bed.SaveAsync(detail, Content(Value(data: "2")));
        var id = detail.ProfileId.ToString();

        var restored = await _functions.RestoreSetupProfileVersion(Request("POST", new { version = 1, baseVersion = 2, actor = new { upn = "ops@contoso.com" } }), id, FunctionTestHost.Context);
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await restored.ReadJsonAsync();
        json.GetProperty("currentVersion").GetInt32().Should().Be(3);
        json.GetProperty("content").GetProperty("registryValues")[0].GetProperty("data").GetString().Should().Be("1");

        (await _functions.RestoreSetupProfileVersion(Request("POST", new { version = 1, baseVersion = 2, actor = new { upn = "ops@contoso.com" } }), id, FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _functions.RestoreSetupProfileVersion(Request("POST", new { version = 9, baseVersion = 3, actor = new { upn = "ops@contoso.com" } }), id, FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _functions.RestoreSetupProfileVersion(Request("POST", new { version = 1, baseVersion = 3, actor = new { upn = "ops@contoso.com" } }), "not-a-guid", FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Versions_AreNewestFirst_WithCurrentAndInUse()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        await _bed.SaveAsync(detail, Content(Value(data: "2")));
        await _bed.AddSessionAsync(SessionState.SessionInProgress, _bed.Stockholm, detail.ProfileId, 1);

        var json = await (await _functions.GetSetupProfileVersions(Request("GET"), detail.ProfileId.ToString(), FunctionTestHost.Context)).ReadJsonAsync();

        json.EnumerateArray().Select(v => (v.GetProperty("version").GetInt32(), v.GetProperty("isCurrent").GetBoolean(), v.GetProperty("inUseCount").GetInt32()))
            .Should().Equal((2, true, 0), (1, false, 1));
        (await _functions.GetSetupProfileVersions(Request("GET"), Guid.NewGuid().ToString(), FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OneVersion_IncludesItsContent_AndAnUnknownOneIs404()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        await _bed.SaveAsync(detail, Content(Value(data: "2")));
        var id = detail.ProfileId.ToString();

        var json = await (await _functions.GetSetupProfileVersion(Request("GET"), id, "1", FunctionTestHost.Context)).ReadJsonAsync();
        json.GetProperty("content").GetProperty("registryValues")[0].GetProperty("data").GetString().Should().Be("1");

        (await _functions.GetSetupProfileVersion(Request("GET"), id, "7", FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _functions.GetSetupProfileVersion(Request("GET"), id, "0", FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _functions.GetSetupProfileVersion(Request("GET"), id, "x", FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task History_IsNewestFirst_WithTheKindsOfChange()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        _bed.Time.Advance(TimeSpan.FromMinutes(1));
        await _bed.SaveAsync(detail, targeting: At(_bed.Stockholm, _bed.Gothenburg), name: "Stockholm and Gothenburg");

        var json = await (await _functions.GetSetupProfileHistory(Request("GET"), detail.ProfileId.ToString(), FunctionTestHost.Context)).ReadJsonAsync();

        var kinds = json.EnumerateArray().Select(h => h.GetProperty("kind").GetString()).ToList();
        kinds.Should().Contain(["Created", "Renamed", "TargetingChanged"]);
        kinds.IndexOf("Created").Should().BeGreaterThan(kinds.IndexOf("Renamed"), "the newest entry comes first");
        (await _functions.GetSetupProfileHistory(Request("GET"), Guid.NewGuid().ToString(), FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
