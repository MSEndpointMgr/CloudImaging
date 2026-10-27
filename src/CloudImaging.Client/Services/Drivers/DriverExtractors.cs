using System.IO;
using System.IO.Compression;
using CloudImaging.Client.Services.Setup;
using CloudImaging.Contracts.Models.Drivers;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services.Drivers;

/// <summary>Unpacks a vendor package into a folder.</summary>
public interface IDriverExtractor
{
    /// <summary>The package kind this extractor handles.</summary>
    DriverExtractKind Kind { get; }

    /// <summary>Extracts <paramref name="packagePath"/> into <paramref name="outputDirectory"/>; true when anything was produced.</summary>
    Task<bool> ExtractAsync(string packagePath, string outputDirectory, CancellationToken ct);
}

/// <summary>Adds drivers to the applied Windows image.</summary>
public interface IDismDriverInjector
{
    /// <summary>Runs DISM against the applied volume; returns its exit code (-1 when it did not run to completion).</summary>
    Task<int> AddDriversAsync(string volumeRoot, string driverDirectory, CancellationToken ct);
}

/// <summary>
/// Extracts HP SoftPaqs with their silent extract switches, through the restricted process runner
/// with a 10-minute limit. Older SoftPaqs ignore <c>/e /f</c>; when nothing was produced the
/// legacy switches are tried. TEMP points inside the component's folder, so the package writes
/// nothing outside it.
/// </summary>
public sealed partial class HpSoftPaqExtractor(IRestrictedProcessRunner processes, ILogger logger) : IDriverExtractor
{
    /// <summary>Time limit per extraction.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <inheritdoc/>
    public DriverExtractKind Kind => DriverExtractKind.Hp;

    /// <inheritdoc/>
    public Task<bool> ExtractAsync(string packagePath, string outputDirectory, CancellationToken ct) => Task.Run(() =>
    {
        Directory.CreateDirectory(outputDirectory);
        if (Run(packagePath, $"/s /e /f \"{outputDirectory}\"", outputDirectory, ct) && HasFiles(outputDirectory))
        {
            return true;
        }

        Run(packagePath, $"-pdf -e -s -f\"{outputDirectory}\"", outputDirectory, ct);
        return HasFiles(outputDirectory);
    }, ct);

    private bool Run(string packagePath, string arguments, string outputDirectory, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(packagePath)!;
        var temp = Path.Combine(folder, "tmp");
        Directory.CreateDirectory(temp);
        var result = processes.Run(new RestrictedProcessRequest(packagePath, arguments, folder, new Dictionary<string, string> { ["TEMP"] = temp, ["TMP"] = temp }, Timeout), ct);
        // CA1873: the logged values are computed up front, not inside the logging call.
        var packageName = Path.GetFileName(packagePath);
        var loggedArguments = arguments.Replace(outputDirectory, "<x>", StringComparison.OrdinalIgnoreCase);
        LogExtract(logger, packageName, loggedArguments, result.Outcome, result.ExitCode);
        return result.Outcome == RestrictedProcessOutcome.Exited;
    }

    internal static bool HasFiles(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any();

    [LoggerMessage(Level = LogLevel.Information, Message = "Extracted {Package} with {Arguments}: {Outcome}, exit code {ExitCode}.")]
    private static partial void LogExtract(ILogger logger, string package, string arguments, RestrictedProcessOutcome outcome, int? exitCode);
}

/// <summary>
/// Extracts a Dell 64-bit update package's drivers with <c>/s /drivers="&lt;x&gt;"</c>, through the
/// restricted process runner with a 10-minute limit and TEMP inside the component's folder.
/// </summary>
public sealed partial class DellPackageExtractor(IRestrictedProcessRunner processes, ILogger logger) : IDriverExtractor
{
    /// <inheritdoc/>
    public DriverExtractKind Kind => DriverExtractKind.Dell64;

    /// <inheritdoc/>
    public Task<bool> ExtractAsync(string packagePath, string outputDirectory, CancellationToken ct) => Task.Run(() =>
    {
        Directory.CreateDirectory(outputDirectory);
        var folder = Path.GetDirectoryName(packagePath)!;
        var temp = Path.Combine(folder, "tmp");
        Directory.CreateDirectory(temp);
        var result = processes.Run(new RestrictedProcessRequest(packagePath, $"/s /drivers=\"{outputDirectory}\"", folder, new Dictionary<string, string> { ["TEMP"] = temp, ["TMP"] = temp }, HpSoftPaqExtractor.Timeout), ct);
        var packageName = Path.GetFileName(packagePath);
        LogExtract(logger, packageName, result.Outcome, result.ExitCode);
        return result.Outcome == RestrictedProcessOutcome.Exited && HpSoftPaqExtractor.HasFiles(outputDirectory);
    }, ct);

    [LoggerMessage(Level = LogLevel.Information, Message = "Extracted {Package} with /s /drivers: {Outcome}, exit code {ExitCode}.")]
    private static partial void LogExtract(ILogger logger, string package, RestrictedProcessOutcome outcome, int? exitCode);
}

/// <summary>
/// Unpacks a custom component ZIP in process. Every entry must stay inside the output folder; one
/// that would land outside it fails the whole package.
/// </summary>
public sealed class ZipPackageExtractor : IDriverExtractor
{
    /// <summary>Most entries a package may have.</summary>
    public const int MaxEntries = 20_000;

    /// <summary>Most bytes a package may expand to.</summary>
    public const long MaxExpandedBytes = 20L * 1024 * 1024 * 1024;

    /// <inheritdoc/>
    public DriverExtractKind Kind => DriverExtractKind.Zip;

    /// <inheritdoc/>
    public Task<bool> ExtractAsync(string packagePath, string outputDirectory, CancellationToken ct) => Task.Run(() =>
    {
        var root = Path.GetFullPath(outputDirectory.EndsWith(Path.DirectorySeparatorChar) ? outputDirectory : outputDirectory + Path.DirectorySeparatorChar);
        using var archive = ZipFile.OpenRead(packagePath);

        // A package that would flood the Windows volume is refused before anything is written.
        if (archive.Entries.Count > MaxEntries || archive.Entries.Sum(e => e.Length) > MaxExpandedBytes)
        {
            throw new InvalidDataException("The package expands to more than a driver package may.");
        }

        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The entry {entry.FullName} would be written outside the package folder.");
            }
        }

        Directory.CreateDirectory(root);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // The declared length is enforced while writing, so a lying header cannot expand further.
            using var source = entry.Open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > entry.Length)
                {
                    throw new InvalidDataException($"The entry {entry.FullName} is larger than the package says.");
                }

                output.Write(buffer, 0, read);
            }
        }

        return HpSoftPaqExtractor.HasFiles(root);
    }, ct);
}

/// <summary>
/// Adds the drivers under a folder to the applied Windows volume with
/// <c>dism.exe /Image:&lt;W&gt;:\ /Add-Driver /Driver:"&lt;x&gt;" /Recurse</c>. Never
/// <c>/ForceUnsigned</c>, and never against an image file: stored OS images stay unchanged.
/// </summary>
public sealed partial class DismDriverInjector(IRestrictedProcessRunner processes, ILogger logger) : IDismDriverInjector
{
    /// <summary>Time limit per DISM run.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    /// <summary>DISM arguments for a volume root (<c>W:\</c>) and a driver folder.</summary>
    public static string Arguments(string volumeRoot, string driverDirectory) =>
        $"/Image:{volumeRoot} /Add-Driver /Driver:\"{driverDirectory}\" /Recurse";

    /// <inheritdoc/>
    public Task<int> AddDriversAsync(string volumeRoot, string driverDirectory, CancellationToken ct) => Task.Run(() =>
    {
        var result = processes.Run(new RestrictedProcessRequest(Path.Combine(Environment.SystemDirectory, "dism.exe"), Arguments(volumeRoot, driverDirectory), driverDirectory, new Dictionary<string, string>(), Timeout), ct);
        foreach (var line in result.Output)
        {
            LogDismOutput(logger, line);
        }

        LogDismFinished(logger, result.Outcome, result.ExitCode);
        return result.Outcome == RestrictedProcessOutcome.Exited ? result.ExitCode ?? -1 : -1;
    }, ct);

    [LoggerMessage(Level = LogLevel.Debug, Message = "DISM: {Line}")]
    private static partial void LogDismOutput(ILogger logger, string line);

    [LoggerMessage(Level = LogLevel.Information, Message = "DISM Add-Driver finished: {Outcome}, exit code {ExitCode}.")]
    private static partial void LogDismFinished(ILogger logger, RestrictedProcessOutcome outcome, int? exitCode);
}
