using System.Diagnostics;
using System.Globalization;
using System.IO;
using CloudImaging.Contracts.Models;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>One thing wrong with a Windows RE source, and what to do about it.</summary>
/// <param name="Problem">What was found.</param>
/// <param name="Fix">What the technician should change.</param>
public sealed record SourceProblem(string Problem, string Fix);

/// <summary>Outcome of checking a Windows installation source for a Windows RE boot image.</summary>
/// <param name="Summary">What was found, shown when every check passes (e.g. "Build 26100.4061").</param>
/// <param name="Problems">Every blocking problem; empty when the source can be used.</param>
public sealed record SourceCheckResult(string Summary, IReadOnlyList<SourceProblem> Problems)
{
    /// <summary>True when the source can be used.</summary>
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// A Windows RE boot image is built from the <c>Winre.wim</c> inside a Windows installation source
/// (an ISO or its <c>sources\install.wim</c>), plus the two DLLs Windows RE needs for Wi-Fi, which
/// only the full OS carries. The source must match the Windows ADK, because the ADK's optional
/// components are added to it.
/// </summary>
public static class WindowsRecoverySource
{
    /// <summary>DLLs the WLAN stack in Windows RE needs from the full OS.</summary>
    public static readonly string[] WifiDlls = ["dmcmnutils.dll", "mdmregistration.dll"];

    /// <summary>
    /// Compares the source with the Windows ADK and the target architecture. Pure, so every
    /// combination can be tested without an ISO or an ADK install.
    /// </summary>
    public static IReadOnlyList<SourceProblem> Evaluate(WimImageInfo source, WimImageInfo? adk, IReadOnlyCollection<string> adkLanguages, MachineArchitecture target)
    {
        var problems = new List<SourceProblem>();
        if (source.Build is { } build && adk?.Build is { } adkBuild && build != adkBuild)
        {
            problems.Add(new SourceProblem(
                string.Create(CultureInfo.InvariantCulture, $"Windows image build {build} doesn't match Windows ADK build {adkBuild}."),
                string.Create(CultureInfo.InvariantCulture, $"Use a build {adkBuild} source.")));
        }

        var sourceArchitecture = source.ProcessorArchitecture is { } raw ? WimMetadataReader.ToMachineArchitecture(raw) : null;
        if (source.ProcessorArchitecture is { } rawArch && sourceArchitecture != target)
        {
            problems.Add(new SourceProblem(
                $"This source is {WimMetadataReader.DescribeProcessorArchitecture(rawArch)}, but the target architecture is {Display(target)}.",
                $"Pick {(target == MachineArchitecture.Arm64 ? "an ARM64" : "an x64")} source or switch the target architecture."));
        }

        if (source.DefaultLanguage is { Length: > 0 } language && !adkLanguages.Contains(language, StringComparer.OrdinalIgnoreCase))
        {
            var available = adkLanguages.FirstOrDefault() ?? "en-US";
            problems.Add(new SourceProblem(
                $"Windows image language ({language}) doesn't match the Windows ADK ({available}).",
                $"Use an {available} source, or install the {language} Windows PE language packs."));
        }

        return problems;
    }

    /// <summary>
    /// Checks an ISO or install.wim. Mounts an ISO read-only for the duration of the check and
    /// detaches it again unless it was already attached.
    /// </summary>
    public static async Task<SourceCheckResult> CheckAsync(string sourcePath, MachineArchitecture target, CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath))
            return Fail("The file does not exist.", "Choose a Windows ISO or install.wim.");

        var adkPath = BootImageGenerationService.FindAdkPath();
        if (adkPath is null)
            return Fail("The Windows ADK with the Windows PE add-on is not installed.", "Install both, then check again.");

        try
        {
            await using var source = await OpenAsync(sourcePath, ct);
            if (source.InstallWimPath is null)
            {
                return Fail(
                    source.HasInstallEsd ? "This ISO has only sources\\install.esd, which cannot be mounted." : "This ISO has no sources\\install.wim.",
                    "Use an ISO that includes install.wim, such as a volume licensing or Visual Studio download.");
            }

            var info = await WimMetadataReader.ReadImageInfoAsync(source.InstallWimPath, ct);
            if (info is null)
                return Fail("The install.wim could not be read.", "Choose another Windows installation source.");

            var adkWim = AdkWinPeWimPath(adkPath, target);
            var adkInfo = adkWim is not null ? await WimMetadataReader.ReadImageInfoAsync(adkWim, ct) : null;
            var problems = Evaluate(info, adkInfo, AdkLanguages(adkPath, target), target);
            var summary = string.Create(CultureInfo.InvariantCulture,
                $"Build {info.Build}.{info.ServicePackBuild}, {(info.ProcessorArchitecture is { } a ? WimMetadataReader.DescribeProcessorArchitecture(a) : "unknown")}, {info.DefaultLanguage}. Matches the Windows ADK.");
            return new SourceCheckResult(summary, problems);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail($"The source could not be opened: {ex.Message}", "Choose another Windows installation source.");
        }
    }

    /// <summary>
    /// Copies Winre.wim and the Wi-Fi DLLs out of install.wim index 1 into <paramref name="destinationDir"/>.
    /// Runs elevated; the ISO and the install.wim mount are recorded so an interrupted run is undone.
    /// </summary>
    /// <returns>Path of the extracted Winre.wim.</returns>
    public static async Task<string> ExtractAsync(string sourcePath, string workDir, string destinationDir, Func<string, CancellationToken, Task> runDism, CancellationToken ct)
    {
        if (sourcePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            MountCleanup.RecordDiskImage(workDir, sourcePath);

        await using var source = await OpenAsync(sourcePath, ct);
        var installWim = source.InstallWimPath
            ?? throw new InvalidOperationException("The Windows installation source has no sources\\install.wim.");

        var mountDir = Path.Combine(workDir, "InstallMount");
        Directory.CreateDirectory(mountDir);
        Directory.CreateDirectory(destinationDir);
        MountCleanup.RecordWimMount(workDir, mountDir);
        var mounted = true;
        try
        {
            await runDism($"/Mount-Image /ImageFile:\"{installWim}\" /Index:1 /MountDir:\"{mountDir}\" /ReadOnly", ct);

            var winre = Path.Combine(mountDir, "Windows", "System32", "Recovery", "Winre.wim");
            if (!File.Exists(winre))
                throw new InvalidOperationException("The Windows image has no Windows\\System32\\Recovery\\Winre.wim. Choose another Windows installation source.");

            var winreCopy = Path.Combine(destinationDir, "winre.wim");
            File.Copy(winre, winreCopy, overwrite: true);
            File.SetAttributes(winreCopy, FileAttributes.Normal);

            foreach (var dll in WifiDlls)
            {
                var path = Path.Combine(mountDir, "Windows", "System32", dll);
                if (!File.Exists(path))
                    throw new InvalidOperationException($"The Windows image has no {dll}, which Wi-Fi in Windows RE needs. Choose another Windows installation source.");
                var copy = Path.Combine(destinationDir, dll);
                File.Copy(path, copy, overwrite: true);
                File.SetAttributes(copy, FileAttributes.Normal);
            }

            await runDism($"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", ct);
            mounted = false;
            MountCleanup.Forget(workDir, mountDir);
            return winreCopy;
        }
        finally
        {
            if (mounted)
            {
                try { await runDism($"/Unmount-Image /MountDir:\"{mountDir}\" /Discard", CancellationToken.None); }
                catch { /* the sweep at the start of the next run undoes it */ }
            }
            if (sourcePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                MountCleanup.Forget(workDir, sourcePath);
        }
    }

    /// <summary>The ADK's own Windows PE image, whose build the source must match.</summary>
    public static string? AdkWinPeWimPath(string adkPath, MachineArchitecture architecture)
    {
        var path = Path.Combine(adkPath, "Windows Preinstallation Environment", MachineArchitecturePlatform.AdkArchitectureName(architecture), "en-us", "winpe.wim");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Languages the ADK has optional-component language packs for, e.g. en-us.</summary>
    public static IReadOnlyCollection<string> AdkLanguages(string adkPath, MachineArchitecture architecture)
    {
        var ocs = Path.Combine(adkPath, "Windows Preinstallation Environment", MachineArchitecturePlatform.AdkArchitectureName(architecture), "WinPE_OCs");
        if (!Directory.Exists(ocs))
            return [];
        return [.. Directory.GetDirectories(ocs)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name.Contains('-', StringComparison.Ordinal) && File.Exists(Path.Combine(ocs, name, $"WinPE-WMI_{name}.cab")))];
    }

    private static string Display(MachineArchitecture architecture) => architecture == MachineArchitecture.Arm64 ? "ARM64" : "x64";

    private static SourceCheckResult Fail(string problem, string fix) => new(string.Empty, [new SourceProblem(problem, fix)]);

    private static async Task<OpenedSource> OpenAsync(string sourcePath, CancellationToken ct)
    {
        if (!sourcePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            return new OpenedSource(sourcePath, null, false, false);

        var (root, wasAttached) = await AttachIsoAsync(sourcePath, ct);
        var wim = Path.Combine(root, "sources", "install.wim");
        var esd = Path.Combine(root, "sources", "install.esd");
        return new OpenedSource(File.Exists(wim) ? wim : null, wasAttached ? null : sourcePath, wasAttached, File.Exists(esd));
    }

    private static async Task<(string Root, bool WasAttached)> AttachIsoAsync(string isoPath, CancellationToken ct)
    {
        var literal = isoPath.Replace("'", "''", StringComparison.Ordinal);
        var script = $"$i = Get-DiskImage -ImagePath '{literal}'; $was = $i.Attached; if (-not $was) {{ $i = Mount-DiskImage -ImagePath '{literal}' -StorageType ISO -Access ReadOnly -PassThru }}; $l = ($i | Get-Volume).DriveLetter; \"$was|$l\"";
        var output = await RunPowerShellAsync(script, ct);
        var parts = output.Trim().Split('|');
        if (parts.Length != 2 || parts[1].Length != 1)
            throw new InvalidOperationException("Windows could not mount the ISO.");
        return ($"{parts[1]}:\\", string.Equals(parts[0], "True", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"", StringComparison.Ordinal)}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException((await stderr).Trim() is { Length: > 0 } error ? error : "PowerShell failed.");
        return await stdout;
    }

    private sealed class OpenedSource(string? installWimPath, string? isoToDetach, bool wasAlreadyAttached, bool hasInstallEsd) : IAsyncDisposable
    {
        public string? InstallWimPath { get; } = installWimPath;

        public bool WasAlreadyAttached { get; } = wasAlreadyAttached;

        public bool HasInstallEsd { get; } = hasInstallEsd;

        public async ValueTask DisposeAsync()
        {
            if (isoToDetach is null)
                return;
            try
            {
                using var process = Process.Start(new ProcessStartInfo("powershell.exe", MountCleanup.DismountDiskImageArguments(isoToDetach)) { UseShellExecute = false, CreateNoWindow = true });
                if (process is not null)
                    await process.WaitForExitAsync();
            }
            catch { /* the sweep at the start of the next elevated run detaches it */ }
        }
    }
}
