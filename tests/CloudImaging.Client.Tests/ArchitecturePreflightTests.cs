using CloudImaging.Client.Services;
using CloudImaging.Contracts.Models;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>Architecture checks that gate destructive disk work (todo/arm64-support.md, Milestone 2 §5).</summary>
public sealed class ArchitecturePreflightTests
{
    [Theory]
    [InlineData(MachineArchitecture.X64, MachineArchitecture.X64)]
    [InlineData(MachineArchitecture.Arm64, MachineArchitecture.Arm64)]
    public void MatchingOsImage_Passes(MachineArchitecture device, MachineArchitecture image)
    {
        ArchitecturePreflight.CheckOsImage(device, image).Should().BeNull();
    }

    [Fact]
    public void MismatchedOsImage_FailsBeforeTheDiskIsTouched()
    {
        ArchitecturePreflight.CheckOsImage(MachineArchitecture.Arm64, MachineArchitecture.X64)
            .Should().Contain("image is x64").And.Contain("device is ARM64").And.Contain("disk was not modified");
    }

    [Fact]
    public void LegacyBackendWithoutImageArchitecture_IsTreatedAsX64()
    {
        ArchitecturePreflight.CheckOsImage(MachineArchitecture.X64, null).Should().BeNull();
        ArchitecturePreflight.CheckOsImage(MachineArchitecture.Arm64, null).Should().NotBeNull();
    }

    [Theory]
    [InlineData(MachineArchitecture.Arm64, MachineArchitecture.Arm64, true)]
    [InlineData(MachineArchitecture.Arm64, MachineArchitecture.X64, false)]
    [InlineData(MachineArchitecture.X64, null, true)]
    [InlineData(MachineArchitecture.Arm64, null, false)]
    public void RecoveryImage_MustMatchDevice(MachineArchitecture device, MachineArchitecture? recovery, bool compatible)
    {
        ArchitecturePreflight.IsRecoveryImageCompatible(device, recovery).Should().Be(compatible);
    }
}
