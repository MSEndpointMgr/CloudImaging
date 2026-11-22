using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services.Drivers;

public sealed class DriverStartingRulesTests
{
    private readonly DriverSyncHarness _h = new();

    [Fact]
    public async Task EnsureStartingRules_AddsTheSkipRules_AndLogsThem()
    {
        await _h.PolicyService.EnsureStartingRulesAsync();

        var rules = (await _h.Bed.Store.Rules.ListPartitionAsync(DriverStore.RulePartition)).Select(r => r.Value).ToList();
        rules.Select(r => r.Name).Should().BeEquivalentTo("BIOS", "Firmware", "Dock firmware", "TPM firmware");
        rules.Should().OnlyContain(r => r.Action == DriverAction.Skip && r.ChangedBy == DriverPolicyService.StartingRulesActor);
        (await _h.Bed.Store.GetSyncSettingsAsync()).Value.StartingRulesAdded.Should().BeTrue();
        var log = (await _h.Bed.Store.ChangeLog.ListPartitionAsync(DriverStore.LogPartition)).Select(r => r.Value).ToList();
        log.Where(l => l.Area == "Rule").Should().HaveCount(4);
    }

    [Fact]
    public async Task EnsureStartingRules_ADeletedRule_StaysDeleted()
    {
        await _h.PolicyService.EnsureStartingRulesAsync();
        var bios = DriverPolicyService.StartingRules(_h.Bed.Time.GetUtcNow()).Single(r => r.Name == "BIOS");
        await _h.Bed.Store.Rules.DeleteAsync(DriverStore.RulePartition, bios.RuleId.ToString("D"));

        await _h.PolicyService.EnsureStartingRulesAsync();

        (await _h.Bed.Store.Rules.ListPartitionAsync(DriverStore.RulePartition)).Should().NotContain(r => r.Value.Name == "BIOS");
    }

    [Fact]
    public async Task EnsureStartingRules_ARuleWithTheSameName_IsKept()
    {
        var mine = new DriverRuleRecord { RuleId = Guid.NewGuid(), Name = "Firmware", Scope = DriverRuleScope.Hp, Match = DriverRuleMatch.Category, Value = "Firmware", Action = DriverAction.Specialize, ChangedBy = "admin@contoso.com" };
        await _h.Bed.Store.Rules.UpsertAsync(DriverStore.RulePartition, mine.RuleId.ToString("D"), mine);

        await _h.PolicyService.EnsureStartingRulesAsync();

        var firmware = (await _h.Bed.Store.Rules.ListPartitionAsync(DriverStore.RulePartition)).Where(r => r.Value.Name == "Firmware").ToList();
        firmware.Should().ContainSingle().Which.Value.Should().Be(mine);
    }

    [Theory]
    [InlineData(DriverVendor.Hp, "HP BIOS and System Firmware (Y88)", "BIOS - System Firmware", "BIOS")]
    [InlineData(DriverVendor.Hp, "Sandisk and Western Digital SSD Firmware Update", "Firmware", "Firmware")]
    [InlineData(DriverVendor.Hp, "All Docks - Ethernet Driver (Realtek)", "Dock - Firmware and Driver", "Dock firmware")]
    [InlineData(DriverVendor.Hp, "Nuvoton Trusted Platform Module (TPM) Firmware", "Software - Security", "TPM firmware")]
    [InlineData(DriverVendor.Dell, "Dell Precision 3490 and Latitude 5450 System BIOS", "BIOS - BIOS", "BIOS")]
    [InlineData(DriverVendor.Dell, "Dell Universal Dock UD22 Firmware Update Utility", "Firmware - Docks/Stands", "Firmware")]
    public void StartingRules_SkipFirmware(DriverVendor vendor, string name, string category, string ruleName)
    {
        var input = new DriverPolicyInput(vendor, "id", name, category, [DriverAction.Skip, DriverAction.Specialize, DriverAction.Offline], DriverAction.Offline);

        var decision = DriverPolicyResolver.Resolve(input, null, DriverPolicyService.StartingRules(_h.Bed.Time.GetUtcNow()));

        decision.Action.Should().Be(DriverAction.Skip);
        decision.RuleName.Should().Be(ruleName);
    }

    [Theory]
    [InlineData(DriverVendor.Dell, "Dell Wireless 5932e and Qualcomm Snapdragon X62 Firmware and GNSS/GPS Driver", "Driver - Communications")]
    [InlineData(DriverVendor.Dell, "Dell ControlVault3 Plus Driver and Firmware", "Driver - Security")]
    [InlineData(DriverVendor.Hp, "Seamless Firmware Update Service", "Driver - Enabling")]
    public void StartingRules_LeaveDriversThatMentionFirmwareAlone(DriverVendor vendor, string name, string category)
    {
        var input = new DriverPolicyInput(vendor, "id", name, category, [DriverAction.Offline, DriverAction.Skip], DriverAction.Offline);

        DriverPolicyResolver.Resolve(input, null, DriverPolicyService.StartingRules(_h.Bed.Time.GetUtcNow())).Source.Should().Be(DriverActionSource.Default);
    }
}
