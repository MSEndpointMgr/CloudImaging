using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.SetupProfiles;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services.Setup;

/// <summary>What Prepare setup works from: the pinned profile (if any) and the applied volume.</summary>
public sealed record PrepareSetupInput(
    string? ProfileName,
    SetupProfileContent? Content,
    IReadOnlyList<SpecializeInstaller> SpecializeInstallers,
    string VolumeRoot,
    MachineArchitecture Architecture)
{
    /// <summary>Session the work belongs to, recorded in the setup runner manifest.</summary>
    public Guid SessionId { get; init; }

    /// <summary>Pinned profile version, recorded in the setup runner manifest.</summary>
    public int? ProfileVersion { get; init; }

    /// <summary>Downloaded script files (UTF-8 with BOM) by SHA-256, relative to the applied volume.</summary>
    public IReadOnlyDictionary<string, string> ScriptFiles { get; init; } = new Dictionary<string, string>();

    /// <summary>The profile's verified answer file text, or null.</summary>
    public string? AnswerFileText { get; init; }
}

/// <summary>The step's result as it is reported to the portal.</summary>
public sealed record PrepareSetupResult(ImagingStepStatus Status, int WarningCount, IReadOnlyList<ImagingStepItem> Items, string? ErrorDetail);

/// <summary>
/// The Prepare setup step: writes the profile's registry values to the applied image, runs the
/// Offline scripts, stages Specialize work for the setup runner, then writes the answer file
/// Windows setup reads. Every item gets an outcome. Under Complete with warnings a failed item only
/// adds a warning; under Fail the deployment the step stops at the first failure and reports every
/// later item as not run.
/// </summary>
public sealed partial class PrepareSetupService(
    IOfflineRegistryHive hive,
    ISetupFileSystem files,
    IWinPeScriptRunner scripts,
    Func<byte[]> runnerScript,
    TimeProvider time,
    ILogger logger)
{
    /// <summary>Where Windows setup looks for the answer file first.</summary>
    public const string AnswerFilePath = @"Windows\Panther\unattend.xml";

    /// <summary>Answer files Windows setup would read instead of <see cref="AnswerFilePath"/>, removed before writing.</summary>
    public static readonly string[] CompetingAnswerFiles = [@"Windows\Panther\Unattend\unattend.xml", @"Windows\Panther\Unattend\autounattend.xml"];

    /// <summary>Generalization state Sysprep /generalize leaves behind.</summary>
    private const int Generalized = 7;

    private const int MaxErrorDetailLength = 500;

    /// <summary>Reason when a script's text could not be downloaded to the applied volume.</summary>
    public const string TextMissingReason = "The script could not be downloaded. Check the device's network connection.";

    /// <summary>Runs the step and reports status lines and step percent through <paramref name="progress"/>.</summary>
    public PrepareSetupResult Run(PrepareSetupInput input, Action<string, int> progress, CancellationToken cancellationToken = default)
    {
        var content = input.Content;
        var failDeployment = content?.FailureHandling == SetupFailureHandling.FailDeployment;
        var registryValues = content?.RegistryValues ?? [];
        var winPeScripts = ScriptsFor(content, SetupScriptPhase.WinPE);
        var setupScripts = ScriptsFor(content, SetupScriptPhase.WindowsSetup);
        var installers = input.SpecializeInstallers;
        var total = registryValues.Count + winPeScripts.Count + installers.Count + setupScripts.Count + 1;
        var items = new List<ImagingStepItem>(total);
        bool Stopped() => failDeployment && items.Any(i => i.Outcome == StepItemOutcome.Failed);
        int? generalizationState;
        int processed;
        int queued;
        try
        {
            (generalizationState, processed, queued) = RunPhases(input, progress, items, registryValues, winPeScripts, installers, setupScripts, total, failDeployment, cancellationToken);
        }
        finally
        {
            // The downloaded texts and Offline script files are not part of the installed Windows, also when a phase throws.
            if (winPeScripts.Count > 0 || input.ScriptFiles.Count > 0)
            {
                RemoveWorkFolder();
            }
        }

        // Phase 4: the answer file is written whenever the step runs, so an answer file baked into
        // the OS image never applies to a device this step prepared.
        progress("Writing the answer file", Percent(processed, total));
        items.Add(Stopped()
            ? Item(items.Count, StepItemKind.AnswerFile, "Answer file", AnswerFilePath, StepItemOutcome.NotRun, OfflineRegistryWriter.NotRunReason)
            : WriteAnswerFile(items.Count, input, hasWindowsSetupWork: queued > 0, generalizationState));
        progress("Writing the answer file", 100);

        var failed = items.Where(i => i.Outcome == StepItemOutcome.Failed).ToList();
        if (failDeployment && failed.Count > 0)
        {
            var first = failed[0];
            var detail = $"{first.Name}: {first.Reason}";
            return new PrepareSetupResult(ImagingStepStatus.Failed, failed.Count, items, detail.Length > MaxErrorDetailLength ? detail[..MaxErrorDetailLength] : detail);
        }
        return new PrepareSetupResult(ImagingStepStatus.Completed, failed.Count, items, null);
    }

    /// <summary>
    /// Registry values, Offline scripts and Specialize staging, adding an item for each. Returns the
    /// image's generalization state, the items processed and how many items the setup runner will start.
    /// </summary>
    private (int? GeneralizationState, int Processed, int Queued) RunPhases(
        PrepareSetupInput input, Action<string, int> progress, List<ImagingStepItem> items, IReadOnlyList<SetupRegistryValue> registryValues,
        List<SetupScript> winPeScripts, IReadOnlyList<SpecializeInstaller> installers, List<SetupScript> setupScripts, int total, bool failDeployment,
        CancellationToken cancellationToken)
    {
        var processed = 0;
        bool Stopped() => failDeployment && items.Any(i => i.Outcome == StepItemOutcome.Failed);

        // Phase 1: registry values. Runs even without values, because the SYSTEM hive load is also
        // what clears Setup\UnattendFile and reads the generalization state.
        if (registryValues.Count > 0)
        {
            progress("Writing registry values", 0);
        }
        var written = new OfflineRegistryWriter(hive, logger).Write(registryValues, input.VolumeRoot, failDeployment, () => progress("Writing registry values", Percent(++processed, total)));
        var generalizationState = written.GeneralizationState;
        foreach (var outcome in written.Values)
        {
            items.Add(Item(items.Count, StepItemKind.RegistryValue, outcome.Value.Name, OfflineRegistryPath.DisplayTarget(outcome.Value), outcome.Outcome, outcome.Reason));
        }

        // Phase 2: Offline scripts, in profile order, after every hive is unloaded.
        var windowsDrive = input.VolumeRoot.TrimEnd('\\');
        for (var i = 0; i < winPeScripts.Count; i++)
        {
            var script = winPeScripts[i];
            var line = string.Create(CultureInfo.InvariantCulture, $"Running Offline scripts ({i + 1} of {winPeScripts.Count})");
            if (Stopped())
            {
                items.Add(Item(items.Count, StepItemKind.WinPEScript, script.Name, null, StepItemOutcome.NotRun, OfflineRegistryWriter.NotRunReason));
            }
            else if (!input.ScriptFiles.TryGetValue(script.ContentSha256, out var textPath))
            {
                items.Add(Item(items.Count, StepItemKind.WinPEScript, script.Name, null, StepItemOutcome.Failed, TextMissingReason));
            }
            else
            {
                progress(line, Percent(processed, total));
                var result = scripts.Run(script, textPath, i + 1, windowsDrive, cancellationToken);
                items.Add(Item(items.Count, StepItemKind.WinPEScript, script.Name, null, result.Outcome, result.Reason) with
                {
                    ExitCode = result.ExitCode,
                    DurationMs = result.DurationMs,
                    LastOutputLine = Truncate(result.LastOutputLine, ImagingStepItem.MaxLastOutputLineLength),
                });
            }
            progress(line, Percent(++processed, total));
        }

        // Phase 3: Specialize work for the setup runner. Driver installers come first, because
        // network drivers and companion apps must be in place before the profile's own scripts.
        var queued = 0;
        if (installers.Count > 0 || setupScripts.Count > 0)
        {
            if (Stopped())
            {
                foreach (var installer in installers)
                {
                    items.Add(Item(items.Count, StepItemKind.DriverInstaller, installer.Name, null, StepItemOutcome.NotRun, OfflineRegistryWriter.NotRunReason));
                }
                foreach (var script in setupScripts)
                {
                    items.Add(Item(items.Count, StepItemKind.WindowsSetupScript, script.Name, null, StepItemOutcome.NotRun, OfflineRegistryWriter.NotRunReason));
                }
                processed += installers.Count + setupScripts.Count;
            }
            else
            {
                progress("Staging Specialize work", Percent(processed, total));
                queued = StageWindowsSetupWork(input, installers, setupScripts, items, failDeployment);
                processed += installers.Count + setupScripts.Count;
                progress("Staging Specialize work", Percent(processed, total));
            }
        }
        return (generalizationState, processed, queued);
    }

    private static List<SetupScript> ScriptsFor(SetupProfileContent? content, SetupScriptPhase phase) =>
        [.. (content?.Scripts ?? []).Where(s => s.Runs == phase).OrderBy(s => s.Order)];

    /// <summary>
    /// Verifies the handed-over installers, writes the scripts, the runner and the manifest, and
    /// adds one item per installer and script. Returns how many items the runner will start; when
    /// none is left, the staging folder is removed so the answer file gets no runner command.
    /// </summary>
    private int StageWindowsSetupWork(PrepareSetupInput input, IReadOnlyList<SpecializeInstaller> installers, List<SetupScript> setupScripts, List<ImagingStepItem> items, bool failDeployment)
    {
        bool Stopped() => failDeployment && items.Any(i => i.Outcome == StepItemOutcome.Failed);
        var readyInstallers = new List<SpecializeInstaller>();
        var installerItems = new List<int>();
        foreach (var installer in installers)
        {
            if (Stopped())
            {
                items.Add(Item(items.Count, StepItemKind.DriverInstaller, installer.Name, null, StepItemOutcome.NotRun, OfflineRegistryWriter.NotRunReason));
                continue;
            }
            var missing = installer.Files.FirstOrDefault(f => !files.FileExists(f.Path));
            installerItems.Add(items.Count);
            if (missing is not null)
            {
                items.Add(Item(items.Count, StepItemKind.DriverInstaller, installer.Name, null, StepItemOutcome.Failed, $"A file this installer needs is missing: {missing.Path}."));
                continue;
            }
            readyInstallers.Add(installer);
            items.Add(Item(items.Count, StepItemKind.DriverInstaller, installer.Name, null, StepItemOutcome.Queued, null));
        }

        var staged = new List<StagedScript>();
        var scriptItems = new List<int>();
        try
        {
            files.CreateDirectory(SetupRunnerManifestBuilder.ScriptFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStagingFailed(logger, ex.Message);
        }
        for (var i = 0; i < setupScripts.Count; i++)
        {
            var script = setupScripts[i];
            if (Stopped())
            {
                items.Add(Item(items.Count, StepItemKind.WindowsSetupScript, script.Name, null, StepItemOutcome.NotRun, OfflineRegistryWriter.NotRunReason));
                continue;
            }
            var path = SetupRunnerManifestBuilder.ScriptPath(i + 1);
            scriptItems.Add(items.Count);
            if (!input.ScriptFiles.TryGetValue(script.ContentSha256, out var textPath))
            {
                items.Add(Item(items.Count, StepItemKind.WindowsSetupScript, script.Name, null, StepItemOutcome.Failed, TextMissingReason));
                continue;
            }
            try
            {
                files.CopyFile(textPath, path);
                string sha256;
                using (var written = files.OpenRead(path))
                {
                    sha256 = Convert.ToHexStringLower(SHA256.HashData(written));
                }
                staged.Add(new StagedScript(script.Name, path, sha256, script.TimeoutMinutes));
                items.Add(Item(items.Count, StepItemKind.WindowsSetupScript, script.Name, null, StepItemOutcome.Queued, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                items.Add(Item(items.Count, StepItemKind.WindowsSetupScript, script.Name, null, StepItemOutcome.Failed, $"The script could not be staged: {ex.Message}"));
            }
        }

        if (readyInstallers.Count + staged.Count == 0)
        {
            RemoveStaging();
            return 0;
        }

        try
        {
            files.WriteAllBytes(SetupRunnerManifestBuilder.RunnerPath, runnerScript());
            var manifest = SetupRunnerManifestBuilder.Build(input.SessionId, time.GetUtcNow(), input.ProfileName, input.ProfileVersion, readyInstallers, staged);
            files.WriteAllBytes(SetupRunnerManifestBuilder.ManifestPath, SetupRunnerManifestBuilder.Serialize(manifest));
            return manifest.Items.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Without the runner or its manifest nothing queued would run, so every queued item fails.
            LogStagingFailed(logger, ex.Message);
            var reason = $"The setup runner could not be staged: {ex.Message}";
            foreach (var index in installerItems.Concat(scriptItems).Where(index => items[index].Outcome == StepItemOutcome.Queued))
            {
                items[index] = items[index] with { Outcome = StepItemOutcome.Failed, Reason = Truncate(reason, ImagingStepItem.MaxReasonLength) };
            }
            RemoveStaging();
            return 0;
        }
    }

    private void RemoveWorkFolder()
    {
        try
        {
            files.DeleteTree(WinPeScriptRunner.WorkFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStagingFailed(logger, ex.Message);
        }
    }

    private void RemoveStaging()
    {
        try
        {
            files.DeleteTree(SetupRunnerManifestBuilder.StagingFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStagingFailed(logger, ex.Message);
        }
    }

    private ImagingStepItem WriteAnswerFile(int index, PrepareSetupInput input, bool hasWindowsSetupWork, int? generalizationState)
    {
        if (hasWindowsSetupWork && generalizationState is not Generalized)
        {
            return Item(index, StepItemKind.AnswerFile, "Answer file", AnswerFilePath, StepItemOutcome.Failed,
                "The OS image is not generalized, so Windows setup will not run the setup runner. Capture the image after Sysprep /generalize.");
        }

        byte[] bytes;
        try
        {
            bytes = UnattendComposer.Compose(input.AnswerFileText, hasWindowsSetupWork, input.Architecture);
        }
        catch (XmlException ex)
        {
            return Item(index, StepItemKind.AnswerFile, "Answer file", AnswerFilePath, StepItemOutcome.Failed, $"The answer file could not be read: {ex.Message}");
        }

        try
        {
            files.CreateDirectory(@"Windows\Panther");
            foreach (var competing in CompetingAnswerFiles)
            {
                files.DeleteFile(competing);
            }
            files.WriteAllBytes(AnswerFilePath, bytes);

            // Read back what landed on disk: the runner command must be there exactly when needed.
            var written = UnattendComposer.Parse(Encoding.UTF8.GetString(files.ReadAllBytes(AnswerFilePath)));
            if (UnattendComposer.CountRunnerCommands(written) != (hasWindowsSetupWork ? 1 : 0))
            {
                return Item(index, StepItemKind.AnswerFile, "Answer file", AnswerFilePath, StepItemOutcome.Failed, "The answer file on disk does not match what was written.");
            }
            LogAnswerFileWritten(logger, AnswerFilePath, bytes.Length);
            return Item(index, StepItemKind.AnswerFile, "Answer file", AnswerFilePath, StepItemOutcome.Succeeded, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            LogAnswerFileFailed(logger, ex.Message);
            var reason = input.ProfileName is null && input.SpecializeInstallers.Count > 0
                ? $"The answer file could not be written, so these driver installers will not run: {string.Join(", ", input.SpecializeInstallers.Select(i => i.Name))}."
                : $"The answer file could not be written: {ex.Message}";
            return Item(index, StepItemKind.AnswerFile, "Answer file", AnswerFilePath, StepItemOutcome.Failed, reason);
        }
    }

    private static int Percent(int processed, int total) => total == 0 ? 100 : Math.Min(100, processed * 100 / total);

    private static ImagingStepItem Item(int index, StepItemKind kind, string name, string? target, StepItemOutcome outcome, string? reason) => new()
    {
        Index = index,
        Kind = kind,
        Name = Truncate(name, ImagingStepItem.MaxNameLength)!,
        Target = Truncate(target, ImagingStepItem.MaxTargetLength),
        Outcome = outcome,
        Reason = Truncate(reason, ImagingStepItem.MaxReasonLength),
    };

    private static string? Truncate(string? text, int max) => text is null || text.Length <= max ? text : text[..max];

    [LoggerMessage(Level = LogLevel.Information, Message = "Answer file written to {Path} ({Bytes} bytes).")]
    private static partial void LogAnswerFileWritten(ILogger logger, string path, int bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The answer file could not be written: {Reason}")]
    private static partial void LogAnswerFileFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows setup work could not be staged: {Reason}")]
    private static partial void LogStagingFailed(ILogger logger, string reason);
}
