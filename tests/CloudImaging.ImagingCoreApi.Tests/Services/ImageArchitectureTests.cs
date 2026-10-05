using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Domain;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services;

/// <summary>OS/recovery image and session architecture handling (todo/arm64-support.md, Milestone 2).</summary>
public sealed class ImageArchitectureTests
{
    [Theory]
    [InlineData("""{}""", true, null)]
    [InlineData("""{"architecture":null}""", true, null)]
    [InlineData("""{"architecture":"x64"}""", true, MachineArchitecture.X64)]
    [InlineData("""{"architecture":"arm64"}""", true, MachineArchitecture.Arm64)]
    [InlineData("""{"architecture":"ARM64"}""", false, null)]
    [InlineData("""{"architecture":"x86"}""", false, null)]
    [InlineData("""{"architecture":12}""", false, null)]
    public void TryReadRequested_AcceptsOnlyWireSlugs(string json, bool valid, MachineArchitecture? expected)
    {
        using var doc = JsonDocument.Parse(json);

        ImageArchitecture.TryReadRequested(doc.RootElement, out var requested).Should().Be(valid);
        requested.Should().Be(expected);
    }

    [Fact]
    public void Resolve_UsesDetectedArchitecture_WhenNothingWasSelected()
    {
        ImageArchitecture.Resolve(12, null, "OS image", out var resolved).Should().BeNull();
        resolved.Should().Be(MachineArchitecture.Arm64);
    }

    [Fact]
    public void Resolve_RejectsSelectionThatContradictsTheImage()
    {
        ImageArchitecture.Resolve(9, MachineArchitecture.Arm64, "OS image", out _)
            .Should().Contain("does not match the OS image, which targets x64");
    }

    [Fact]
    public void Resolve_RejectsX86Images()
    {
        ImageArchitecture.Resolve(0, null, "recovery image", out _).Should().Contain("x86");
    }

    [Fact]
    public void Resolve_KeepsSelection_WhenImageRecordsNoArchitecture()
    {
        ImageArchitecture.Resolve(null, MachineArchitecture.Arm64, "OS image", out var resolved).Should().BeNull();
        resolved.Should().Be(MachineArchitecture.Arm64);
    }

    [Fact]
    public void MismatchMessage_NamesBothArchitectures()
    {
        var mismatch = new ArchitectureMismatchException(MachineArchitecture.Arm64, [(Guid.NewGuid(), MachineArchitecture.X64), (Guid.NewGuid(), MachineArchitecture.X64)]);

        mismatch.Message.Should().Be("This OS image is ARM64, but 2 devices are x64. Assign an image built for the device's architecture.");
    }

    [Fact]
    public void RecoveryPlanPublish_IsScopedToArchitecture()
    {
        var active = Enumerable.Range(0, 5)
            .Select(i => Recovery(MachineArchitecture.X64, i, latest: i == 0))
            .Append(Recovery(MachineArchitecture.Arm64, 1, latest: true))
            .ToList();

        var (toDemote, toClearLatest) = RecoveryImageRepository.PlanPublish(active, MachineArchitecture.Arm64);

        toDemote.Should().BeEmpty();
        toClearLatest.Should().ContainSingle().Which.Architecture.Should().Be(MachineArchitecture.Arm64);
    }

    [Fact]
    public void NewSession_TakesArchitectureFromRegistration_AndDefaultsLegacyClientsToX64()
    {
        var arm = DeviceSessionFactory.CreateNew(Registration(MachineArchitecture.Arm64), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)).Session;
        var legacy = DeviceSessionFactory.CreateNew(Registration(null), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)).Session;

        arm.Architecture.Should().Be(MachineArchitecture.Arm64);
        legacy.Architecture.Should().Be(MachineArchitecture.X64);
    }

    private static DeviceRegistrationPayload Registration(MachineArchitecture? architecture) => new()
    {
        SerialNumber = "SN1",
        Manufacturer = "Contoso",
        Model = "Arm Laptop",
        Architecture = architecture,
    };

    private static RecoveryImage Recovery(MachineArchitecture architecture, int daysAgo, bool latest) => new()
    {
        RecoveryImageId = Guid.NewGuid(),
        Version = $"1.0.{daysAgo}",
        StoragePath = "recovery-images/winre.wim",
        Sha256Hash = "abc",
        UploadedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        Architecture = architecture,
        IsLatestPublished = latest,
        IsActive = true,
    };
}
