using System.IO;
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
    MachineArchitecture Architecture);

/// <summary>The step's result as it is reported to the portal.</summary>
public sealed record PrepareSetupResult(ImagingStepStatus Status, int WarningCount, IReadOnlyList<ImagingStepItem> Items, string? ErrorDetail);

/// <summary>
/// The Prepare setup step: writes the profile's registry values to the applied image, then writes
/// the answer file Windows setup reads. Every item gets an outcome. Under Complete with warnings a
/// failed item only adds a warning; under Fail the deployment the step stops at the first failure
/// and reports every later item as not run.
/// </summary>
public sealed partial class PrepareSetupService(IOfflineRegistryHive hive, ISetupFileSystem files, ILogger logger)
{
    /// <summary>Where Windows setup looks for the answer file first.</summary>
    public const string AnswerFilePath = @"Windows\Panther\unattend.xml";

    /// <summary>Answer files Windows setup would read instead of <see cref="AnswerFilePath"/>, removed before writing.</summary>
    public static readonly string[] CompetingAnswerFiles = [@"Windows\Panther\Unattend\unattend.xml", @"Windows\Panther\Unattend\autounattend.xml"];

    /// <summary>Generalization state Sysprep /generalize leaves behind.</summary>
    private const int Generalized = 7;

    private const int MaxErrorDetailLength = 500;

    /// <summary>Runs the step and reports status lines and step percent through <paramref name="progress"/>.</summary>
    public PrepareSetupResult Run(PrepareSetupInput input, Action<string, int> progress)
    {
        var content = input.Content;
        var failDeployment = content?.FailureHandling == SetupFailureHandling.FailDeployment;
        var registryValues = content?.RegistryValues ?? [];
        var total = registryValues.Count + 1;
        var processed = 0;
        var items = new List<ImagingStepItem>(total);

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
        var stopped = failDeployment && written.Values.Any(v => v.Outcome == StepItemOutcome.Failed);

        // Phase 4: the answer file is written whenever the step runs, so an answer file baked into
        // the OS image never applies to a device this step prepared.
        progress("Writing the answer file", Percent(processed, total));
        items.Add(stopped
            ? Item(items.Count, StepItemKind.AnswerFile, "Answer file", AnswerFilePath, StepItemOutcome.NotRun, OfflineRegistryWriter.NotRunReason)
            : WriteAnswerFile(items.Count, input, hasWindowsSetupWork: false, generalizationState));
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
            bytes = UnattendComposer.Compose(input.Content?.AnswerFile?.Content, hasWindowsSetupWork, input.Architecture);
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
}
