using System.Diagnostics;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services.Drivers;

public sealed class DriverPolicyResolverTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly DriverPolicyInput Wlan = new(
        DriverVendor.Hp, "sp150123", "Intel Wireless LAN Driver", "Driver - Network",
        [DriverAction.Offline, DriverAction.Skip], DriverAction.Offline);

    private static readonly DriverPolicyInput Audio = new(
        DriverVendor.Hp, "sp151002", "Realtek Audio Driver", "Driver - Audio",
        [DriverAction.OfflineAndSpecialize, DriverAction.Offline, DriverAction.Skip], DriverAction.OfflineAndSpecialize);

    [Fact]
    public void Resolve_OnlySkipAllowed_IsLocked()
    {
        var locked = Wlan with { AllowedActions = [DriverAction.Skip], DefaultAction = DriverAction.Skip };

        var decision = DriverPolicyResolver.Resolve(locked, DriverAction.Offline, [Rule("Everything offline", DriverRuleMatch.NamePattern, "*", DriverAction.Offline)]);

        decision.Should().Be(new DriverPolicyDecision(DriverAction.Skip, DriverActionSource.Locked));
    }

    [Fact]
    public void Resolve_Override_BeatsRules()
    {
        var decision = DriverPolicyResolver.Resolve(Wlan, DriverAction.Skip, [Rule("Keep network", DriverRuleMatch.Category, "Driver - Network", DriverAction.Offline)]);

        decision.Source.Should().Be(DriverActionSource.Override);
        decision.Action.Should().Be(DriverAction.Skip);
    }

    [Fact]
    public void Resolve_VendorRule_BeatsAllVendorRule()
    {
        var rules = new[]
        {
            Rule("All vendors skip network", DriverRuleMatch.ComponentId, "sp150123", DriverAction.Skip, DriverRuleScope.AllVendors),
            Rule("HP network offline", DriverRuleMatch.Category, "Driver - Network", DriverAction.Offline, DriverRuleScope.Hp),
        };

        var decision = DriverPolicyResolver.Resolve(Wlan, null, rules);

        decision.RuleName.Should().Be("HP network offline");
    }

    [Fact]
    public void Resolve_AllVendorRule_BeatsDefault()
    {
        var decision = DriverPolicyResolver.Resolve(Wlan, null, [Rule("Skip network", DriverRuleMatch.Category, "driver - network", DriverAction.Skip, DriverRuleScope.AllVendors)]);

        decision.Should().Be(new DriverPolicyDecision(DriverAction.Skip, DriverActionSource.Rule, "Skip network", decision.RuleId));
    }

    [Fact]
    public void Resolve_NoRule_UsesTheDefault()
    {
        DriverPolicyResolver.Resolve(Audio, null, []).Should().Be(new DriverPolicyDecision(DriverAction.OfflineAndSpecialize, DriverActionSource.Default));
    }

    [Fact]
    public void Resolve_SameLayer_IdBeatsNameBeatsCategory()
    {
        var rules = new[]
        {
            Rule("By category", DriverRuleMatch.Category, "Driver - Audio", DriverAction.Skip),
            Rule("By name", DriverRuleMatch.NamePattern, "Realtek*", DriverAction.Offline),
            Rule("By id", DriverRuleMatch.ComponentId, "SP151002", DriverAction.OfflineAndSpecialize),
        };

        DriverPolicyResolver.Resolve(Audio, null, rules).RuleName.Should().Be("By id");
        DriverPolicyResolver.Resolve(Audio, null, rules[..2]).RuleName.Should().Be("By name");
    }

    [Fact]
    public void Resolve_SameLayerAndKind_MostRecentlyChangedWins()
    {
        var rules = new[]
        {
            Rule("Older", DriverRuleMatch.NamePattern, "*Audio*", DriverAction.Skip, changedAt: T0),
            Rule("Newer", DriverRuleMatch.NamePattern, "Realtek*", DriverAction.Offline, changedAt: T0.AddDays(1)),
        };

        DriverPolicyResolver.Resolve(Audio, null, rules).RuleName.Should().Be("Newer");
    }

    [Fact]
    public void Resolve_DisallowedAction_IsIgnoredAndTheNextLayerDecides()
    {
        var rules = new[]
        {
            Rule("Specialize network", DriverRuleMatch.ComponentId, "sp150123", DriverAction.Specialize, DriverRuleScope.Hp),
            Rule("Skip network everywhere", DriverRuleMatch.Category, "Driver - Network", DriverAction.Skip, DriverRuleScope.AllVendors),
        };

        var decision = DriverPolicyResolver.Resolve(Wlan, DriverAction.Specialize, rules);

        decision.RuleName.Should().Be("Skip network everywhere");
        decision.Action.Should().Be(DriverAction.Skip);
    }

    [Fact]
    public void Resolve_RuleForOtherVendor_DoesNotApply()
    {
        var decision = DriverPolicyResolver.Resolve(Wlan, null, [Rule("Dell skip", DriverRuleMatch.Category, "Driver - Network", DriverAction.Skip, DriverRuleScope.Dell)]);

        decision.Source.Should().Be(DriverActionSource.Default);
    }

    [Theory]
    [InlineData("HP Services Scan for Commercial", "HP Services Scan*", true)]
    [InlineData("HP Services Scan for Commercial", "*services scan*", true)]
    [InlineData("HP App", "HP App", true)]
    [InlineData("HP Application", "HP App", false)]
    [InlineData("Seamless Firmware Update Service", "*Firmware*Service", true)]
    [InlineData("Seamless Firmware Update", "*Firmware*Service", false)]
    [InlineData("abc", "a*b*c", true)]
    [InlineData("ac", "a*b*c", false)]
    [InlineData("a.b(c)", "a.b(c)", true)]
    public void MatchesNamePattern_Wildcards_AreCaseInsensitive(string name, string pattern, bool expected)
    {
        DriverPolicyResolver.MatchesNamePattern(pattern, name).Should().Be(expected);
    }

    [Fact]
    public void MatchesNamePattern_TenThousandCharacterName_RunsInLinearTime()
    {
        var name = new string('a', 10_000);
        var pattern = string.Concat(Enumerable.Repeat("*a", 100)) + "*b";

        var watch = Stopwatch.StartNew();
        var result = DriverPolicyResolver.MatchesNamePattern(pattern, name);
        watch.Stop();

        result.Should().BeFalse();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(250));
    }

    private static DriverRuleRecord Rule(string name, DriverRuleMatch match, string value, DriverAction action, DriverRuleScope scope = DriverRuleScope.Hp, DateTimeOffset? changedAt = null) => new()
    {
        RuleId = Guid.NewGuid(),
        Name = name,
        Scope = scope,
        Match = match,
        Value = value,
        Action = action,
        ChangedAt = changedAt ?? T0,
    };
}
