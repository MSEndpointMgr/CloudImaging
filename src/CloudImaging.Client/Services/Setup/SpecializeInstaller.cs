namespace CloudImaging.Client.Services.Setup;

/// <summary>A file an installer needs, relative to the volume root, with its expected hash.</summary>
public sealed record SpecializeInstallerFile(string Path, string Sha256);

/// <summary>How the setup runner starts an installer.</summary>
public sealed record SpecializeInstallerCommand(string Executable, string Arguments, string WorkingDirectory);

/// <summary>
/// An installer that must run during Windows setup (specialize), already staged on the applied
/// volume by the step before Prepare setup. Prepare setup verifies the files exist, lists it in the
/// setup runner manifest before any script, and reports it as a DriverInstaller item.
/// </summary>
public sealed record SpecializeInstaller(
    string Name,
    IReadOnlyList<SpecializeInstallerFile> Files,
    SpecializeInstallerCommand Command,
    int TimeoutSeconds,
    IReadOnlyList<int> SuccessExitCodes,
    IReadOnlyList<string> CleanupPaths);
