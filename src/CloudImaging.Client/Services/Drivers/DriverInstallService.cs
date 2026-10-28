using System.IO;
using System.Security.Cryptography;
using CloudImaging.Client.Services.Setup;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.Contracts.Models.SetupProfiles;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services.Drivers;

/// <summary>A status line and step percent while drivers are installed.</summary>
/// <param name="Status">Status line.</param>
/// <param name="Percent">Step percent.</param>
public sealed record DriverInstallProgress(string Status, int Percent);

/// <summary>Result of the Install drivers step.</summary>
/// <param name="Status">Completed, or Failed when a critical component failed.</param>
/// <param name="WarningCount">Failed components, or 1 for a skipped step with a warning.</param>
/// <param name="Items">One item per component.</param>
/// <param name="StatusNote">Note shown under the step when it was skipped.</param>
/// <param name="ErrorDetail">What failed, the likely cause and the fix, for a critical failure.</param>
/// <param name="Installers">Installers handed to Prepare setup for the specialize pass.</param>
public sealed record DriverInstallResult(ImagingStepStatus Status, int WarningCount, IReadOnlyList<ImagingStepItem> Items, string? StatusNote, string? ErrorDetail, IReadOnlyList<SpecializeInstaller> Installers);

/// <summary>
/// The Install drivers step (contracts/client-install-drivers.md): per component in descriptor
/// order, download and verify into <c>&lt;W&gt;:\CloudImaging\Drivers\&lt;index&gt;-&lt;id&gt;\</c>,
/// extract, add the drivers to the applied volume with DISM, hand Specialize work to Prepare
/// setup, and delete the folder of everything not handed off. Everything is written on the
/// Windows volume only, and all work runs off the UI thread.
/// </summary>
public sealed partial class DriverInstallService
{
    /// <summary>Folder on the Windows volume, relative to its root.</summary>
    public const string StagingFolder = @"CloudImaging\Drivers";

    /// <summary>Extraction failed or produced nothing usable.</summary>
    public const string ExtractFailedMessage = "The package could not be extracted.";

    /// <summary>Too little space to extract.</summary>
    public const string NoSpaceMessage = "The Windows volume does not have enough free space to extract this package.";

    /// <summary>A component for PCI hardware the device does not have.</summary>
    public const string NoHardwareMessage = "No matching hardware in this device.";

    /// <summary>Specialize installers run at most this long.</summary>
    public const int InstallerTimeoutSeconds = 1200;

    private static readonly int[] InstallerSuccessExitCodes = [0, 3010];
    private readonly DriverSetLoader _loader;
    private readonly Dictionary<DriverExtractKind, IDriverExtractor> _extractors;
    private readonly IDismDriverInjector _dism;
    private readonly ILogger _logger;
    private readonly Func<string, long> _freeSpace;
    private readonly IPciInventory _pci;
    private readonly DriverFileCache? _cache;
    private readonly HashSet<int> _fromUsb = [];

    /// <param name="loader">Downloads.</param>
    /// <param name="extractors">Extractors per package kind.</param>
    /// <param name="dism">DISM.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="freeSpace">Free bytes on the volume of a path.</param>
    /// <param name="pci">The device's PCI hardware, read once per step when a component lists PCI IDs.</param>
    /// <param name="cache">Driver files on the USB drive, or null without a CACHE volume.</param>
    public DriverInstallService(DriverSetLoader loader, IEnumerable<IDriverExtractor> extractors, IDismDriverInjector dism, ILogger logger, Func<string, long>? freeSpace = null, IPciInventory? pci = null, DriverFileCache? cache = null)
    {
        _cache = cache is { IsAvailable: true } ? cache : null;
        _loader = loader;
        _extractors = extractors.ToDictionary(e => e.Kind);
        _dism = dism;
        _logger = logger;
        _freeSpace = freeSpace ?? (path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace);
        _pci = pci ?? new PciInventory();
    }

    /// <summary>The note shown for a skipped step.</summary>
    public static string SkipNote(DriverSkipReason reason, string? matchKey, string? manufacturer) => reason switch
    {
        DriverSkipReason.NoMatchingModel when !string.IsNullOrWhiteSpace(matchKey) =>
            $"No onboarded model matches this device ({(manufacturer?.Contains("Dell", StringComparison.OrdinalIgnoreCase) == true ? "system SKU" : "system ID")} {matchKey}).",
        DriverSkipReason.NoMatchingModel => "No onboarded model matches this device.",
        DriverSkipReason.NoWindowsRelease => "The OS image has no Windows release set.",
        DriverSkipReason.ReleaseNotOnboarded => "This model is not onboarded for the image's Windows release.",
        DriverSkipReason.NoDriverSet => "This model has no driver set yet. Run a sync.",
        DriverSkipReason.ArchitectureMismatch => "The OS image's architecture does not match this model.",
        DriverSkipReason.NoneChosen => "No drivers were chosen for this device when imaging started.",
        _ => "No drivers apply to this device.",
    };

    /// <summary>Runs the step.</summary>
    /// <param name="sessionId">Session.</param>
    /// <param name="descriptor">The pinned driver set.</param>
    /// <param name="volumeRoot">Root of the applied Windows volume (<c>W:\</c>).</param>
    /// <param name="manufacturer">Device manufacturer, for the skip note.</param>
    /// <param name="progress">Status lines and percent.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<DriverInstallResult> RunAsync(Guid sessionId, SessionDriverSetDescriptor descriptor, string volumeRoot, string? manufacturer, Action<DriverInstallProgress>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.SkipReason is { } skip)
        {
            return Task.FromResult(new DriverInstallResult(ImagingStepStatus.Completed, skip == DriverSkipReason.NoneChosen ? 0 : 1, [], SkipNote(skip, descriptor.MatchKey, manufacturer), null, []));
        }

        // The whole loop runs on the thread pool: hashing, extraction and DISM never touch the UI thread.
        return Task.Run(() => RunComponentsAsync(sessionId, descriptor.Components, NormalizeRoot(volumeRoot), progress, ct), ct);
    }

    private async Task<DriverInstallResult> RunComponentsAsync(Guid sessionId, IReadOnlyList<SessionDriverComponent> components, string root, Action<DriverInstallProgress>? progress, CancellationToken ct)
    {
        var items = new List<ImagingStepItem>();
        var installers = new List<SpecializeInstaller>();
        var failedCritical = new List<(SessionDriverComponent Component, string Reason)>();
        var total = components.Count;
        IReadOnlyList<PciHardwareId>? hardware = null;
        _fromUsb.Clear();
        _cache?.Prepare();
        for (var position = 0; position < total; position++)
        {
            var component = components[position];
            var counter = $"({position + 1} of {total})";
            if (component.PciIds.Count > 0)
            {
                hardware ??= ReadHardware();
                if (hardware is not null && !PciInventory.Matches(component.PciIds, hardware))
                {
                    items.Add(Item(position, component, StepItemOutcome.Skipped, NoHardwareMessage));
                    continue;
                }
            }

            var cached = _cache?.Contains(component.Sha256) == true;
            progress?.Invoke(new DriverInstallProgress(cached ? $"Copying drivers from the USB drive {counter}" : $"Downloading drivers {counter}", position * 100 / Math.Max(1, total)));
            var (outcome, reason, installer) = await InstallAsync(sessionId, component, root, counter, progress, position, total, ct);
            if (installer is not null)
            {
                installers.Add(installer);
            }

            if (outcome == StepItemOutcome.Failed && component.Critical)
            {
                failedCritical.Add((component, reason!));
            }

            items.Add(Item(position, component, outcome, reason) with { FromUsbCache = outcome == StepItemOutcome.Skipped ? null : _fromUsb.Contains(position) });
        }

        if (installers.Count > 0)
        {
            progress?.Invoke(new DriverInstallProgress("Staging Specialize installers", 100));
        }

        TryDeleteIfEmpty(Path.Combine(root, StagingFolder));
        var failed = items.Count(i => i.Outcome == StepItemOutcome.Failed);
        if (failedCritical.Count > 0)
        {
            return new DriverInstallResult(ImagingStepStatus.Failed, failed, items, null, CriticalFailureDetail(failedCritical), installers);
        }

        return new DriverInstallResult(ImagingStepStatus.Completed, failed, items, null, null, installers);
    }

    private static ImagingStepItem Item(int position, SessionDriverComponent component, StepItemOutcome outcome, string? reason) => new()
    {
        Index = position,
        Kind = StepItemKind.DriverComponent,
        Name = Truncate(component.Name, 100),
        Target = component.ComponentId,
        Outcome = outcome,
        Reason = reason,
        DriverAction = component.Action,
        ComponentVersion = component.Version,
        DriverOrigin = component.Origin,
        FromUsbCache = outcome == StepItemOutcome.Skipped ? null : false,
    };

    // When the hardware cannot be read every component is processed, as without PCI IDs.
    private IReadOnlyList<PciHardwareId>? ReadHardware()
    {
        try
        {
            return _pci.Read();
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            LogComponentFailed(_logger, "PCI inventory", ex.Message);
            return null;
        }
    }

    private async Task<(StepItemOutcome Outcome, string? Reason, SpecializeInstaller? Installer)> InstallAsync(Guid sessionId, SessionDriverComponent component, string root, string counter, Action<DriverInstallProgress>? progress, int position, int total, CancellationToken ct)
    {
        var relativeFolder = Path.Combine(StagingFolder, $"{component.Index:000}-{SafeName(component.ComponentId)}");
        var folder = Path.Combine(root, relativeFolder);
        var package = Path.Combine(folder, SafeName(component.FileName));
        var extracted = Path.Combine(folder, "x");
        var handedOff = false;
        try
        {
            if (_cache is not null && await _cache.TryCopyAsync(component.Sha256, package, ct))
            {
                _fromUsb.Add(position);
            }
            else
            {
                var download = await _loader.DownloadAsync(sessionId, component, package, ct);
                if (!download.Succeeded)
                {
                    return Fail(component, download.Reason ?? DriverSetLoader.DownloadFailedMessage);
                }

                // The download is verified; a failed cache write only logs.
                if (_cache is not null)
                {
                    await _cache.AddAsync(component.Sha256, package, ct);
                }
            }

            var offline = component.Action is DriverAction.Offline or DriverAction.OfflineAndSpecialize;
            var stagedRoot = folder;
            if (component.Extract != DriverExtractKind.None)
            {
                if (_freeSpace(folder) < component.SizeBytes * 3)
                {
                    return Fail(component, NoSpaceMessage);
                }

                if (!_extractors.TryGetValue(component.Extract, out var extractor) || !await TryExtractAsync(extractor, package, extracted, ct))
                {
                    return Fail(component, ExtractFailedMessage);
                }

                stagedRoot = extracted;
            }

            var hasInf = Directory.Exists(extracted) && Directory.EnumerateFiles(extracted, "*.inf", SearchOption.AllDirectories).Any();
            var command = Command(component, package, stagedRoot);

            // A vendor package that installs its own app runs itself during specialize, so the
            // whole folder (package and extracted drivers) is handed off.
            if (command is null && component.InstallCommand is { } install && string.Equals(install.Executable, Path.GetFileName(package), StringComparison.OrdinalIgnoreCase))
            {
                stagedRoot = folder;
                command = new SpecializeInstallerCommand(package, install.Arguments ?? string.Empty, folder);
            }

            if ((offline && !hasInf) || (component.Action != DriverAction.Offline && command is null))
            {
                return Fail(component, ExtractFailedMessage);
            }

            if (offline)
            {
                progress?.Invoke(new DriverInstallProgress($"Adding drivers {counter}", position * 100 / Math.Max(1, total)));
                var exitCode = await _dism.AddDriversAsync(root, extracted, ct);
                if (exitCode != 0)
                {
                    return Fail(component, $"Windows could not add the drivers (DISM exit code {exitCode}).");
                }
            }

            if (component.Action == DriverAction.Offline)
            {
                return (StepItemOutcome.Succeeded, null, null);
            }

            var installer = Installer(component, root, relativeFolder, stagedRoot, command!);
            handedOff = true;
            return (component.Action == DriverAction.Specialize ? StepItemOutcome.Queued : StepItemOutcome.Succeeded, null, installer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LogComponentFailed(_logger, component.ComponentId, ex.Message);
            return Fail(component, ExtractFailedMessage);
        }
        finally
        {
            if (!handedOff)
            {
                TryDeleteTree(folder);
            }
        }
    }

    private async Task<bool> TryExtractAsync(IDriverExtractor extractor, string package, string extracted, CancellationToken ct)
    {
        try
        {
            return await extractor.ExtractAsync(package, extracted, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            LogComponentFailed(_logger, Path.GetFileName(package), ex.Message);
            return false;
        }
    }

    // The command a Specialize action runs: the descriptor's, or the package itself with /s for
    // a package that is not extracted (Dell 32-bit). Null when the executable is not there.
    private static SpecializeInstallerCommand? Command(SessionDriverComponent component, string package, string stagedRoot)
    {
        if (component.InstallCommand is { } install)
        {
            // The command may only run a file of this package, never another executable on the volume.
            var stagedFull = Path.GetFullPath(stagedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var executable = Path.GetFullPath(Path.Combine(stagedRoot, install.Executable));
            return executable.StartsWith(stagedFull, StringComparison.OrdinalIgnoreCase) && File.Exists(executable)
                ? new SpecializeInstallerCommand(executable, install.Arguments ?? string.Empty, stagedRoot)
                : null;
        }

        return component.Extract == DriverExtractKind.None ? new SpecializeInstallerCommand(package, "/s", stagedRoot) : null;
    }

    private static SpecializeInstaller Installer(SessionDriverComponent component, string root, string relativeFolder, string stagedRoot, SpecializeInstallerCommand command)
    {
        var files = Directory.EnumerateFiles(stagedRoot, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new SpecializeInstallerFile(Path.GetRelativePath(root, path), Sha256(path)))
            .ToList();
        return new SpecializeInstaller(
            component.Name,
            files,
            new SpecializeInstallerCommand(Path.GetRelativePath(root, command.Executable), command.Arguments, Path.GetRelativePath(root, command.WorkingDirectory)),
            InstallerTimeoutSeconds,
            InstallerSuccessExitCodes,
            [relativeFolder]);
    }

    private (StepItemOutcome, string?, SpecializeInstaller?) Fail(SessionDriverComponent component, string reason)
    {
        LogComponentFailed(_logger, component.ComponentId, reason);
        return (StepItemOutcome.Failed, reason, null);
    }

    // What failed, the likely cause and the fix, in at most 500 characters.
    private static string CriticalFailureDetail(List<(SessionDriverComponent Component, string Reason)> failures)
    {
        var names = string.Join(", ", failures.Select(f => f.Component.Name));
        var detail = $"Critical drivers could not be installed: {names}. {failures[0].Reason} Check the network connection and the model's driver set in the portal, then retry.";
        return Truncate(detail, 500);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string NormalizeRoot(string volumeRoot) =>
        volumeRoot.EndsWith(Path.DirectorySeparatorChar) ? volumeRoot : volumeRoot + Path.DirectorySeparatorChar;

    private static string SafeName(string name) => string.Concat(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    private void TryDeleteTree(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind on the volume; nothing depends on it.
            LogCleanupFailed(_logger, folder, ex.Message);
        }
    }

    private void TryDeleteIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An empty folder left on the volume does no harm.
            LogCleanupFailed(_logger, folder, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Driver staging folder {Folder} could not be removed: {Reason}")]
    private static partial void LogCleanupFailed(ILogger logger, string folder, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Driver component {ComponentId} failed: {Reason}")]
    private static partial void LogComponentFailed(ILogger logger, string componentId, string reason);
}
