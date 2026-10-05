using CloudImaging.Contracts.Models;

namespace CloudImaging.Client.Services;

/// <summary>Architecture checks that must pass before any destructive disk work (todo/arm64-support.md, Milestone 2 §5).</summary>
public static class ArchitecturePreflight
{
    /// <summary>Returns a terminal error when the assigned OS image cannot run on this device, otherwise null.</summary>
    public static string? CheckOsImage(MachineArchitecture device, MachineArchitecture? osImage)
    {
        // A gateway/core that predates architecture tracking reports nothing; every such image is x64.
        var image = osImage ?? MachineArchitecture.X64;
        return image == device
            ? null
            : $"The assigned OS image is {Display(image)}, but this device is {Display(device)}. The disk was not modified. Assign an image built for {Display(device)} and retry.";
    }

    /// <summary>True when a published recovery image can be applied to an OS of <paramref name="device"/> architecture.</summary>
    public static bool IsRecoveryImageCompatible(MachineArchitecture device, MachineArchitecture? recoveryImage) =>
        (recoveryImage ?? MachineArchitecture.X64) == device;

    private static string Display(MachineArchitecture architecture) => architecture == MachineArchitecture.Arm64 ? "ARM64" : "x64";
}
