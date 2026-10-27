namespace CloudImaging.ImagingCoreApi.Storage;

/// <summary>
/// Every blob container the API writes to. Declared in one place so startup can create a container
/// an older environment was deployed without, the way the tables are created.
/// </summary>
public static class StorageContainers
{
    /// <summary>Uploaded and published operating system images.</summary>
    public const string OsImages = "os-images";

    /// <summary>Uploaded and published boot images.</summary>
    public const string BootImages = "boot-images";

    /// <summary>Uploaded and published recovery images.</summary>
    public const string RecoveryImages = "recovery-images";

    /// <summary>Setup profile version documents and uploaded script and answer file texts.</summary>
    public const string SetupProfiles = "setup-profiles";

    /// <summary>Tenant branding assets.</summary>
    public const string Branding = "branding";

    /// <summary>Device logs collected from a session.</summary>
    public const string SessionLogs = "session-logs";

    /// <summary>Vendor catalogs, verified driver files, custom components and driver set documents.</summary>
    public const string Drivers = "drivers";

    /// <summary>All of the above, for the startup check.</summary>
    public static IReadOnlyList<string> All { get; } =
        [OsImages, BootImages, RecoveryImages, SetupProfiles, Branding, SessionLogs, Drivers];
}
