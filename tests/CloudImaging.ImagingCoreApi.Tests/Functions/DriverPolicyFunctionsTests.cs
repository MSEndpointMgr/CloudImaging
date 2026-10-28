using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker.Http;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

public sealed class DriverPolicyFunctionsTests
{
    private const string Base = "https://core.test/api/internal/drivers";
    private const string Upn = "operator@contoso.com";
    private static readonly object Actor = new { upn = Upn, objectId = "oid-1" };
    private static readonly string[] Network = ["Driver - Network"];
    private static readonly string[] Slashed = ["Diagnostic/Utility"];
    private static readonly string[] Audio = ["Audio"];
    private readonly DriverSyncHarness _h = new();
    private readonly DriverFunctions _functions;

    public DriverPolicyFunctionsTests()
    {
        _functions = new DriverFunctions(_h.ModelService, _h.PolicyService);
        _h.PublishModels(DriverSyncHarness.CatalogModel("8C26", "HP EliteBook 840 G11"));
        var locked = DriverSyncHarness.Component("sp103", "sp203", "4.0", "HP Hotkey UWP Service", "Software - Enabling", DriverAction.Skip);
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", "sp200", "1.0", "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"),
            DriverSyncHarness.Component("sp102", "sp202", "3.0", "HP Services Scan for Commercial", "Software - Enabling", DriverAction.Skip),
            locked with { Component = locked.Component with { AllowedActions = [DriverAction.Skip] } });
    }

    [Fact]
    public async Task CreateRule_NamePattern_Returns201WithHits_LogsAndMarksScopeDirty()
    {
        var model = await ActiveModelAsync();

        var response = await CreateRuleAsync(new { name = "Skip Realtek", scope = "Hp", match = "NamePattern", value = "realtek*", action = "Skip", actor = Actor });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var rule = await response.ReadJsonAsync();
        rule.GetProperty("hits").GetInt32().Should().Be(1);
        rule.GetProperty("changedBy").GetString().Should().Be(Upn);
        var pointer = (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value;
        pointer.Dirty.Should().BeTrue();
        pointer.PendingChanges.Should().Be(1);
        pointer.BuildAfter.Should().Be(_h.Bed.Time.GetUtcNow() + TimeSpan.FromMinutes(2));
        _h.Bed.Queues.Sent.Should().ContainSingle(m => m.Work.Kind == DriverWorkKind.BuildSet && m.Delay == TimeSpan.FromMinutes(2));
        (await LogAsync()).Should().ContainSingle(l => l.Area == "Rule" && l.Subject == "Skip Realtek" && l.By == Upn);
    }

    [Fact]
    public async Task CreateRule_DellScope_LeavesHpPairsAlone()
    {
        var model = await ActiveModelAsync();

        (await CreateRuleAsync(new { name = "Dell only", scope = "Dell", match = "Category", value = "Audio", action = "Skip", actor = Actor })).StatusCode.Should().Be(HttpStatusCode.Created);

        (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value.Dirty.Should().BeFalse();
    }

    [Theory]
    [InlineData("", "NamePattern", "Realtek*", "name")]
    [InlineData("x101", "NamePattern", "Realtek*", "name")]
    [InlineData("Rule", "NamePattern", "", "value")]
    [InlineData("Rule", "NamePattern", "x201", "value")]
    [InlineData("Rule", "NamePattern", "Real?ek", "value")]
    [InlineData("Rule", "ComponentId", "sp*", "value")]
    [InlineData("Rule", "Category", "", "value")]
    public async Task CreateRule_InvalidField_Is400NamingTheField(string name, string match, string value, string field)
    {
        name = name == "x101" ? new string('n', 101) : name;
        value = value == "x201" ? new string('v', 201) : value;

        var response = await CreateRuleAsync(new { name, scope = "AllVendors", match, value, action = "Skip", actor = Actor });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.ReadJsonAsync();
        body.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("field").GetString()).Should().Contain(field);
        (await _h.Bed.Store.Rules.ListPartitionAsync(DriverStore.RulePartition)).Should().BeEmpty();
    }

    [Fact]
    public async Task CreateRule_UnknownScope_Is400()
    {
        var response = await CreateRuleAsync(new { name = "Rule", scope = "Lenovo", match = "Category", value = "Audio", action = "Skip", actor = Actor });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateRule_NameInUseIgnoringCase_Is409()
    {
        await CreateRuleAsync(new { name = "Skip Realtek", scope = "Hp", match = "NamePattern", value = "Realtek*", action = "Skip", actor = Actor });

        var response = await CreateRuleAsync(new { name = "SKIP REALTEK", scope = "Hp", match = "Category", value = "Driver - Audio", action = "Skip", actor = Actor });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("A rule named \"SKIP REALTEK\" already exists.");
    }

    [Fact]
    public async Task CreateRule_WithoutActor_Is400()
    {
        var response = await CreateRuleAsync(new { name = "Rule", scope = "Hp", match = "Category", value = "Audio", action = "Skip" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("The acting user is missing.");
    }

    [Fact]
    public async Task UpdateRule_ChangesTheRule_LogsAndKeepsItsName()
    {
        await ActiveModelAsync();
        var id = (await (await CreateRuleAsync(new { name = "Audio", scope = "Hp", match = "Category", value = "Driver - Audio", action = "Skip", actor = Actor })).ReadJsonAsync()).GetProperty("ruleId").GetGuid();

        var response = await _functions.UpdateDriverRule(FunctionTestHost.Request("PUT", $"{Base}/rules/{id}", new { name = "Audio", scope = "Hp", match = "Category", value = "Driver - Network", action = "Skip", actor = Actor }), id.ToString(), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rule = await response.ReadJsonAsync();
        rule.GetProperty("value").GetString().Should().Be("Driver - Network");
        rule.GetProperty("hits").GetInt32().Should().Be(1);
        (await LogAsync()).Count(l => l.Area == "Rule").Should().Be(2);
    }

    [Fact]
    public async Task DeleteRule_Returns204_LogsAndMarksDirty_AndUnknownIs404()
    {
        var model = await ActiveModelAsync();
        var id = (await (await CreateRuleAsync(new { name = "Audio", scope = "AllVendors", match = "Category", value = "Driver - Audio", action = "Skip", actor = Actor })).ReadJsonAsync()).GetProperty("ruleId").GetGuid();

        var deleted = await _functions.DeleteDriverRule(FunctionTestHost.Request("DELETE", $"{Base}/rules/{id}?changedBy={Upn}"), id.ToString(), FunctionTestHost.Context);
        var unknown = await _functions.DeleteDriverRule(FunctionTestHost.Request("DELETE", $"{Base}/rules/{id}?changedBy={Upn}"), id.ToString(), FunctionTestHost.Context);

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value.PendingChanges.Should().Be(2);
        (await LogAsync()).Should().Contain(l => l.Area == "Rule" && l.Change.StartsWith("Deleted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rules_ListsHitsOverCurrentComponents()
    {
        await ActiveModelAsync();
        await CreateRuleAsync(new { name = "Network", scope = "AllVendors", match = "Category", value = "Driver - Network", action = "Skip", actor = Actor });
        await CreateRuleAsync(new { name = "Wireless", scope = "Hp", match = "NamePattern", value = "*Wireless*", action = "Offline", actor = Actor });

        var rules = (await (await _functions.GetDriverRules(FunctionTestHost.Request("GET", $"{Base}/rules"), FunctionTestHost.Context)).ReadJsonAsync()).EnumerateArray().ToList();

        rules.Select(r => r.GetProperty("name").GetString()).Should().Equal("Network", "Wireless");

        // The vendor rule wins over the all-vendors rule, so the category rule decides nothing.
        rules[0].GetProperty("hits").GetInt32().Should().Be(0);
        rules[1].GetProperty("hits").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Suggestions_ExcludeRulesThatExist_AndAreNeverInserted()
    {
        await ActiveModelAsync();

        var before = (await (await _functions.GetDriverRuleSuggestions(FunctionTestHost.Request("GET", $"{Base}/rules/suggestions"), FunctionTestHost.Context)).ReadJsonAsync()).EnumerateArray().ToList();
        await CreateRuleAsync(new { name = "HP Services Scan for Commercial", scope = "Hp", match = "NamePattern", value = "HP Services Scan for Commercial*", action = "Skip", actor = Actor });
        var after = (await (await _functions.GetDriverRuleSuggestions(FunctionTestHost.Request("GET", $"{Base}/rules/suggestions"), FunctionTestHost.Context)).ReadJsonAsync()).EnumerateArray().ToList();

        before.Select(s => s.GetProperty("name").GetString()).Should().Equal("HP Services Scan for Commercial", "Seamless Firmware Update Service", "HP App");
        before[0].GetProperty("hits").GetInt32().Should().Be(1);
        before.Should().OnlyContain(s => s.GetProperty("action").GetString() == "Skip" && s.GetProperty("scope").GetString() == "Hp" && s.GetProperty("match").GetString() == "NamePattern");
        after.Select(s => s.GetProperty("name").GetString()).Should().Equal("Seamless Firmware Update Service", "HP App");
        (await _h.Bed.Store.Rules.ListPartitionAsync(DriverStore.RulePartition)).Should().ContainSingle("suggestions are only listed");
    }

    [Fact]
    public async Task Override_WinsOverARule_AndUndoReturnsToTheRule()
    {
        var model = await ActiveModelAsync();
        await CreateRuleAsync(new { name = "Skip Wireless", scope = "Hp", match = "NamePattern", value = "Intel Wireless*", action = "Skip", actor = Actor });

        var set = await SetOverrideAsync(model.ModelId, "sp100", "Offline");
        var undone = await _functions.ClearDriverOverride(FunctionTestHost.Request("DELETE", $"{Base}/models/{model.ModelId}/releases/Windows11_24H2/overrides/sp100?changedBy={Upn}"), model.ModelId.ToString(), "Windows11_24H2", "sp100", FunctionTestHost.Context);

        set.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await set.ReadJsonAsync();
        row.GetProperty("source").GetString().Should().Be("Override");
        row.GetProperty("action").GetString().Should().Be("Offline");
        undone.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await undone.ReadJsonAsync();
        after.GetProperty("source").GetString().Should().Be("Rule");
        after.GetProperty("ruleName").GetString().Should().Be("Skip Wireless");
        after.GetProperty("action").GetString().Should().Be("Skip");
        (await LogAsync()).Count(l => l.Area == "Override").Should().Be(2);
    }

    [Fact]
    public async Task Override_DisallowedAction_Is400_AndLockedComponentIs400()
    {
        var model = await ActiveModelAsync();

        var disallowed = await SetOverrideAsync(model.ModelId, "sp100", "Specialize");
        var locked = await SetOverrideAsync(model.ModelId, "sp103", "Offline");

        disallowed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await disallowed.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("Specialize is not allowed for this component.");
        locked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await locked.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("The vendor's data only allows Skip for this component.");
        (await _h.Bed.Store.Overrides.ListPartitionAsync(DriverTables.Pair(model.ModelId, DriverSyncHarness.Release))).Should().BeEmpty();
    }

    [Fact]
    public async Task Override_UndoWithoutOverride_Is404_AndUnknownComponentIs404()
    {
        var model = await ActiveModelAsync();

        var undo = await _functions.ClearDriverOverride(FunctionTestHost.Request("DELETE", $"{Base}/models/{model.ModelId}/releases/Windows11_24H2/overrides/sp100?changedBy={Upn}"), model.ModelId.ToString(), "Windows11_24H2", "sp100", FunctionTestHost.Context);
        var unknown = await SetOverrideAsync(model.ModelId, "sp999", "Skip");

        undo.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Override_OnADraft_IsStored_ButNothingIsBuilt()
    {
        await _h.EnableAsync();
        var draft = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11", Contracts.Models.Drivers.DriverModelState.Draft);
        await _h.RunAsync(DriverSyncTrigger.Check);
        _h.Bed.Queues.Sent.Clear();

        var response = await SetOverrideAsync(draft.ModelId, "sp100", "Skip");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _h.Bed.Queues.Sent.Should().NotContain(m => m.Work.Kind == DriverWorkKind.BuildSet);
        (await _h.Bed.Pointers.GetAsync(draft.ModelId, DriverSyncHarness.Release)).Should().BeNull();
    }

    [Fact]
    public async Task FiveChangesWithinTwoMinutes_BuildOneSet_TwoMinutesAfterTheLast()
    {
        var model = await ActiveModelAsync();
        var requestsBefore = _h.Bed.Vendor.Requests.Count;

        for (var i = 0; i < 5; i++)
        {
            (await SetOverrideAsync(model.ModelId, "sp100", i % 2 == 0 ? "Skip" : "Offline")).StatusCode.Should().Be(HttpStatusCode.OK);
            _h.Bed.Time.Advance(TimeSpan.FromSeconds(20));
        }

        var page = await (await _functions.GetDriverModel(FunctionTestHost.Request("GET", $"{Base}/models/{model.ModelId}"), model.ModelId.ToString(), FunctionTestHost.Context)).ReadJsonAsync();
        page.GetProperty("pending").GetProperty("changes").GetInt32().Should().Be(5);
        await _h.AdvanceAsync(TimeSpan.FromMinutes(1));
        (await _h.CurrentSetAsync(model.ModelId))!.Version.Should().Be(1, "the last change was less than 2 minutes ago");

        await _h.AdvanceAsync(TimeSpan.FromMinutes(2));

        var pointer = (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value;
        pointer.CurrentVersion.Should().Be(2);
        pointer.LastVersion.Should().Be(2, "five changes make one set");
        pointer.Dirty.Should().BeFalse();
        pointer.PendingChanges.Should().Be(0);
        (await _h.CurrentSetAsync(model.ModelId))!.Items.Should().NotContain(i => i.LineageKey == "sp100");
        (await _h.Bed.Store.SetBuilds.GetAsync(DriverTables.Pair(model.ModelId, DriverSyncHarness.Release), "000002"))!.Value.Trigger.Should().Be("Policy");
        _h.Bed.Vendor.Requests.Count.Should().Be(requestsBefore, "a policy build reads no catalog");
    }

    [Fact]
    public async Task DirtyPair_DownloadsMissingFiles_AndSwitchesOnlyWhenTheyAreVerified()
    {
        var model = await ActiveModelAsync();
        (await SetOverrideAsync(model.ModelId, "sp102", "Offline")).StatusCode.Should().Be(HttpStatusCode.OK);
        var build = _h.Bed.Queues.Drain().Single(m => m.Work.Kind == DriverWorkKind.BuildSet).Work;

        // Early, the message only puts itself back for the rest of the wait.
        _h.Bed.Time.Advance(TimeSpan.FromMinutes(1));
        await _h.Sync.HandleAsync(build);
        _h.Bed.Queues.Drain().Should().ContainSingle(m => m.Work.Kind == DriverWorkKind.BuildSet && m.Delay == TimeSpan.FromMinutes(1));

        _h.Bed.Time.Advance(TimeSpan.FromMinutes(1));
        await _h.Sync.HandleAsync(build);

        var pending = (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value;
        pending.CurrentVersion.Should().Be(1, "devices keep the current set until the new file is verified");
        pending.PendingVersion.Should().Be(2);
        _h.Bed.Queues.Sent.Should().ContainSingle(m => m.Work.Kind == DriverWorkKind.DownloadFile && m.Work.FileKey == "sp202_3.0");

        await _h.PumpAsync();

        (await _h.CurrentSetAsync(model.ModelId))!.Items.Should().Contain(i => i.LineageKey == "sp102");
        (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value.PendingVersion.Should().BeNull();
    }

    [Fact]
    public async Task BuildNow_BuildsAtOnce_AndIs409WhenNothingIsWaiting()
    {
        var model = await ActiveModelAsync();
        var nothing = await BuildNowAsync(model.ModelId);
        await SetOverrideAsync(model.ModelId, "sp100", "Skip");

        var response = await BuildNowAsync(model.ModelId);

        nothing.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _h.Bed.Queues.Sent.Should().Contain(m => m.Work.Kind == DriverWorkKind.BuildSet && (m.Delay ?? TimeSpan.Zero) == TimeSpan.Zero);
        await _h.PumpAsync();
        (await _h.CurrentSetAsync(model.ModelId))!.Version.Should().Be(2);
    }

    [Fact]
    public async Task Categories_NoneCriticalByDefault_AndAChangeMarksNothingDirty()
    {
        var model = await ActiveModelAsync();

        var before = (await (await _functions.GetDriverCategories(FunctionTestHost.Request("GET", $"{Base}/categories?vendor=Hp"), FunctionTestHost.Context)).ReadJsonAsync()).EnumerateArray().ToList();
        var set = await _functions.SetDriverCriticalCategories(FunctionTestHost.Request("PUT", $"{Base}/critical-categories/Hp", new { categories = Network, actor = Actor }), "Hp", FunctionTestHost.Context);

        before.Select(c => c.GetProperty("category").GetString()).Should().Equal("Driver - Audio", "Driver - Network", "Software - Enabling");
        before.Should().OnlyContain(c => !c.GetProperty("critical").GetBoolean());
        set.StatusCode.Should().Be(HttpStatusCode.OK);
        (await set.ReadJsonAsync()).EnumerateArray().Single(c => c.GetProperty("category").GetString() == "Driver - Network").GetProperty("critical").GetBoolean().Should().BeTrue();
        (await _h.Bed.Pointers.GetAsync(model.ModelId, DriverSyncHarness.Release))!.Value.Dirty.Should().BeFalse();
        _h.Bed.Queues.Sent.Should().NotContain(m => m.Work.Kind == DriverWorkKind.BuildSet);
        (await LogAsync()).Should().ContainSingle(l => l.Area == "CriticalCategory" && l.Subject == "HP" && l.Change == "Driver - Network");
    }

    [Fact]
    public async Task CriticalCategories_WithASlash_AreStoredAndCleared()
    {
        await _functions.SetDriverCriticalCategories(FunctionTestHost.Request("PUT", $"{Base}/critical-categories/Hp", new { categories = Slashed, actor = Actor }), "Hp", FunctionTestHost.Context);
        var cleared = await _functions.SetDriverCriticalCategories(FunctionTestHost.Request("PUT", $"{Base}/critical-categories/Hp", new { categories = Array.Empty<string>(), actor = Actor }), "Hp", FunctionTestHost.Context);

        (await _h.Bed.Store.CriticalCategories.ListPartitionAsync("Hp")).Should().BeEmpty();
        (await cleared.ReadJsonAsync()).EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task CriticalCategories_Lenovo_Is400()
    {
        var response = await _functions.SetDriverCriticalCategories(FunctionTestHost.Request("PUT", $"{Base}/critical-categories/Lenovo", new { categories = Audio, actor = Actor }), "Lenovo", FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<DriverModelRecord> ActiveModelAsync()
    {
        await _h.EnableAsync();
        var model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
        (await _h.CurrentSetAsync(model.ModelId))!.Version.Should().Be(1);
        _h.Bed.Queues.Sent.Clear();
        return model;
    }

    private async Task<HttpResponseData> CreateRuleAsync(object body) =>
        await _functions.CreateDriverRule(FunctionTestHost.Request("POST", $"{Base}/rules", body), FunctionTestHost.Context);

    private async Task<HttpResponseData> SetOverrideAsync(Guid modelId, string lineage, string action) =>
        await _functions.SetDriverOverride(FunctionTestHost.Request("PUT", $"{Base}/models/{modelId}/releases/Windows11_24H2/overrides/{lineage}", new { action, actor = Actor }), modelId.ToString(), "Windows11_24H2", lineage, FunctionTestHost.Context);

    private async Task<HttpResponseData> BuildNowAsync(Guid modelId) =>
        await _functions.BuildDriverSetNow(FunctionTestHost.Request("POST", $"{Base}/models/{modelId}/releases/Windows11_24H2/build", new { actor = Actor }), modelId.ToString(), "Windows11_24H2", FunctionTestHost.Context);

    private async Task<List<DriverChangeLogRecord>> LogAsync() =>
        (await _h.Bed.Store.ChangeLog.ListPartitionAsync(DriverStore.LogPartition)).Select(r => r.Value).ToList();
}
