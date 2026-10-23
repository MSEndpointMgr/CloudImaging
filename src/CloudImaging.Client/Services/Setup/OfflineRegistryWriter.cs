using System.IO;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.SetupProfiles;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services.Setup;

/// <summary>What happened to one registry value of the profile.</summary>
public sealed record RegistryValueOutcome(int ProfileIndex, SetupRegistryValue Value, StepItemOutcome Outcome, string? Reason);

/// <summary>Every value's outcome, plus what the SYSTEM hive said about the image.</summary>
public sealed record OfflineRegistryWriteResult(IReadOnlyList<RegistryValueOutcome> Values, int? GeneralizationState);

/// <summary>
/// Writes a profile's registry values to the applied image. Each hive is loaded only when it has
/// values (SYSTEM always, to clear <c>Setup\UnattendFile</c> and read the generalization state),
/// written with every handle closed, and unloaded before the next one, so nothing is left mounted
/// when the step moves on.
/// </summary>
public sealed partial class OfflineRegistryWriter(IOfflineRegistryHive hive, ILogger logger)
{
    /// <summary>Reason given to items skipped after a failure under Fail the deployment.</summary>
    public const string NotRunReason = "Not run because an earlier item failed and this profile fails the deployment.";

    private static readonly SetupRegistryHive[] HiveOrder = [SetupRegistryHive.Software, SetupRegistryHive.System, SetupRegistryHive.DefaultUser];

    /// <summary>
    /// Writes <paramref name="values"/> under the applied volume <paramref name="volumeRoot"/>. With
    /// <paramref name="stopAtFirstFailure"/>, every value after the first failure is reported NotRun.
    /// </summary>
    public OfflineRegistryWriteResult Write(IReadOnlyList<SetupRegistryValue> values, string volumeRoot, bool stopAtFirstFailure, Action? onValueDone = null)
    {
        var outcomes = new List<RegistryValueOutcome>(values.Count);
        int? generalizationState = null;
        var stopped = false;

        foreach (var current in HiveOrder)
        {
            var forHive = values.Select((value, index) => (value, index)).Where(v => v.value.Hive == current).ToList();
            if (forHive.Count == 0 && current != SetupRegistryHive.System)
            {
                continue;
            }

            if (stopped)
            {
                Skip(forHive);
                continue;
            }

            try
            {
                hive.Load(current, Path.Combine(volumeRoot, OfflineRegistryPath.HiveFile(current)));
            }
            catch (OfflineHiveException ex)
            {
                LogHiveNotLoaded(logger, OfflineRegistryPath.MountRoot(current), ex.Message);
                foreach (var (value, index) in forHive)
                {
                    if (stopped)
                    {
                        outcomes.Add(new RegistryValueOutcome(index, value, StepItemOutcome.NotRun, NotRunReason));
                    }
                    else
                    {
                        outcomes.Add(new RegistryValueOutcome(index, value, StepItemOutcome.Failed, ex.Message));
                        stopped = stopAtFirstFailure;
                    }
                    onValueDone?.Invoke();
                }
                continue;
            }

            try
            {
                var currentControlSet = 1;
                if (current == SetupRegistryHive.System)
                {
                    try
                    {
                        currentControlSet = hive.GetValue(current, "Select", "Current") is int selected and > 0 ? selected : 1;
                        generalizationState = hive.GetValue(current, @"Setup\Status\SysprepStatus", "GeneralizationState") as int?;

                        // Windows setup prefers this value over Panther, so an image that carries
                        // one would ignore the answer file this step writes.
                        hive.DeleteValue(current, "Setup", "UnattendFile");
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or OfflineHiveException)
                    {
                        LogSystemHiveNotRead(logger, ex.Message);
                    }
                }

                foreach (var (value, index) in forHive)
                {
                    if (stopped)
                    {
                        outcomes.Add(new RegistryValueOutcome(index, value, StepItemOutcome.NotRun, NotRunReason));
                        onValueDone?.Invoke();
                        continue;
                    }

                    var outcome = WriteOne(current, value, index, currentControlSet);
                    outcomes.Add(outcome);
                    stopped = stopAtFirstFailure && outcome.Outcome == StepItemOutcome.Failed;
                    onValueDone?.Invoke();
                }
            }
            finally
            {
                if (!hive.Unload(current))
                {
                    LogHiveNotUnloaded(logger, OfflineRegistryPath.MountRoot(current));
                }
            }
        }

        return new OfflineRegistryWriteResult(outcomes.OrderBy(o => o.ProfileIndex).ToList(), generalizationState);

        void Skip(List<(SetupRegistryValue value, int index)> skipped)
        {
            foreach (var (value, index) in skipped)
            {
                outcomes.Add(new RegistryValueOutcome(index, value, StepItemOutcome.NotRun, NotRunReason));
                onValueDone?.Invoke();
            }
        }
    }

    private RegistryValueOutcome WriteOne(SetupRegistryHive current, SetupRegistryValue value, int index, int currentControlSet)
    {
        if (!OfflineRegistryPath.TryParseData(value.Type, value.Data, out var data, out var kind))
        {
            return new RegistryValueOutcome(index, value, StepItemOutcome.Failed, "The data does not match the value type.");
        }

        var target = OfflineRegistryPath.DisplayTarget(value);
        try
        {
            hive.SetValue(current, OfflineRegistryPath.MapKey(current, value.Key, currentControlSet), value.ValueName ?? string.Empty, data, kind);
            LogValueWritten(logger, target);
            return new RegistryValueOutcome(index, value, StepItemOutcome.Succeeded, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or ArgumentException or OfflineHiveException)
        {
            LogValueFailed(logger, target, ex.Message);
            return new RegistryValueOutcome(index, value, StepItemOutcome.Failed, $"The value could not be written: {ex.Message}");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Registry value written: {Target}")]
    private static partial void LogValueWritten(ILogger logger, string target);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Registry value {Target} could not be written: {Reason}")]
    private static partial void LogValueFailed(ILogger logger, string target, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The hive {MountRoot} could not be loaded: {Reason}")]
    private static partial void LogHiveNotLoaded(ILogger logger, string mountRoot, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The SYSTEM hive's control set, generalization state or UnattendFile value could not be read or cleared: {Reason}")]
    private static partial void LogSystemHiveNotRead(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The hive {MountRoot} could not be unloaded. The values are already written; the restart releases it.")]
    private static partial void LogHiveNotUnloaded(ILogger logger, string mountRoot);
}
