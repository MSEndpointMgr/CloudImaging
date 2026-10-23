using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;
using static CloudImaging.ImagingCoreApi.Tests.TestDoubles.SetupProfileTestBed;

namespace CloudImaging.ImagingCoreApi.Tests.Services;

public sealed class SetupProfileResolverTests
{
    private readonly SetupProfileTestBed _bed = new();

    [Fact]
    public async Task ResolveAsync_LocationTargeted_ReturnsThatProfile()
    {
        var stockholm = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        await _bed.CreateAsync("Everywhere", AllDevices);

        var resolved = await _bed.Resolver.ResolveAsync(_bed.Stockholm);

        resolved.Should().NotBeNull();
        resolved!.ProfileId.Should().Be(stockholm.ProfileId);
        resolved.Version.Should().Be(1);
        resolved.Name.Should().Be("Stockholm");
    }

    [Fact]
    public async Task ResolveAsync_LocationNotTargeted_FallsBackToAllDevices()
    {
        await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        var everywhere = await _bed.CreateAsync("Everywhere", AllDevices);

        (await _bed.Resolver.ResolveAsync(_bed.Gothenburg))!.ProfileId.Should().Be(everywhere.ProfileId);
        (await _bed.Resolver.ResolveAsync(null))!.ProfileId.Should().Be(everywhere.ProfileId);
    }

    [Fact]
    public async Task ResolveAsync_NothingApplies_ReturnsNull()
    {
        await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));

        (await _bed.Resolver.ResolveAsync(_bed.Gothenburg)).Should().BeNull();
        (await _bed.Resolver.ResolveAsync(null)).Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_DeletedProfile_IsNeverReturned()
    {
        var everywhere = await _bed.CreateAsync("Everywhere", AllDevices);
        await _bed.Service.DeleteAsync(everywhere.ProfileId, "ops@contoso.com");

        (await _bed.Resolver.ResolveAsync(null)).Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_AfterASave_ReturnsTheNewCurrentVersion()
    {
        var stockholm = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm));
        await _bed.SaveAsync(stockholm, Content(Value(data: "0")));

        (await _bed.Resolver.ResolveAsync(_bed.Stockholm))!.Version.Should().Be(2);
    }
}
