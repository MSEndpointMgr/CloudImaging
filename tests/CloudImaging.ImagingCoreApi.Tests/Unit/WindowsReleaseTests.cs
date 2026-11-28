using System.Text.Json;
using CloudImaging.Contracts.Enums;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Unit;

/// <summary>A Windows release as a value: any release Microsoft ships parses, and the stored and wire forms never change.</summary>
public sealed class WindowsReleaseTests
{
    [Theory]
    [InlineData("Windows11_26H2")]
    [InlineData("Windows11Version26H2")]
    [InlineData("windows11_26h2")]
    [InlineData(" Windows11_26H2 ")]
    public void TryParse_ReadsTheWireAndTheStoredForm(string value)
    {
        WindowsRelease.TryParse(value, out var release).Should().BeTrue();

        release.Major.Should().Be(11);
        release.Version.Should().Be("26H2");
        release.WireName.Should().Be("Windows11_26H2");
        release.ToString().Should().Be("Windows11Version26H2");
        release.Label.Should().Be("Windows 11, version 26H2");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Windows12_26H2")]
    [InlineData("Windows 11")]
    [InlineData("Windows11_26H3")]
    [InlineData("Windows11_2026H2")]
    [InlineData("Windows11_")]
    public void TryParse_RefusesWhatIsNotARelease(string? value)
    {
        WindowsRelease.TryParse(value, out _).Should().BeFalse();
    }

    [Fact]
    public void KnownReleases_KeepTheNamesStoredKeysAndBlobNamesHold()
    {
        WindowsRelease.Windows10Version22H2.ToString().Should().Be("Windows10Version22H2");
        WindowsRelease.Windows11Version23H2.ToString().Should().Be("Windows11Version23H2");
        WindowsRelease.Windows11Version24H2.ToString().Should().Be("Windows11Version24H2");
        WindowsRelease.Windows11Version25H2.ToString().Should().Be("Windows11Version25H2");
        WindowsRelease.TryParse("Windows11Version24H2", out var stored).Should().BeTrue();
        stored.Should().Be(WindowsRelease.Windows11Version24H2);
    }

    [Theory]
    [InlineData("Windows 11, version 26H2", "Windows11_26H2")]
    [InlineData("Windows 10, version 22H2", "Windows10_22H2")]
    [InlineData("Feature update to Windows 11, version 24H2", "Windows11_24H2")]
    public void TryParseName_ReadsMicrosoftsName(string name, string expected)
    {
        WindowsRelease.TryParseName(name, out var release).Should().BeTrue();

        release.WireName.Should().Be(expected);
    }

    [Theory]
    [InlineData("Windows 11")]
    [InlineData("Windows Server 2025")]
    [InlineData(null)]
    public void TryParseName_RefusesANameWithoutARelease(string? name)
    {
        WindowsRelease.TryParseName(name, out _).Should().BeFalse();
    }

    [Fact]
    public void Order_IsByWindowsVersionThenRelease()
    {
        WindowsRelease.TryParse("Windows11_26H2", out var next).Should().BeTrue();

        new[] { WindowsRelease.Windows11Version24H2, next, WindowsRelease.Windows10Version22H2, WindowsRelease.Windows11Version23H2 }.Order()
            .Should().Equal(WindowsRelease.Windows10Version22H2, WindowsRelease.Windows11Version23H2, WindowsRelease.Windows11Version24H2, next);
        (WindowsRelease.Windows11Version25H2 < next).Should().BeTrue();
        (WindowsRelease.Windows10Version22H2 >= WindowsRelease.Windows11Version23H2).Should().BeFalse();
    }

    [Fact]
    public void Json_UsesTheWireForm_AsValueAndAsDictionaryKey()
    {
        WindowsRelease.TryParse("Windows11_26H2", out var next).Should().BeTrue();
        var value = new Holder(next, null, new Dictionary<WindowsRelease, int> { [next] = 1 });

        var json = JsonSerializer.Serialize(value);

        json.Should().Be("""{"Release":"Windows11_26H2","Optional":null,"ByRelease":{"Windows11_26H2":1}}""");
        JsonSerializer.Deserialize<Holder>(json).Should().BeEquivalentTo(value);
    }

    [Fact]
    public void Json_AnUnknownValue_IsAJsonException()
    {
        var read = () => JsonSerializer.Deserialize<Holder>("""{"Release":"Windows12_26H2"}""");

        read.Should().Throw<JsonException>();
    }

    private sealed record Holder(WindowsRelease Release, WindowsRelease? Optional, Dictionary<WindowsRelease, int> ByRelease);
}
