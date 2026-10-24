using System.ComponentModel;
using System.Globalization;
using System.IO;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.SetupProfiles;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services.Setup;

/// <summary>How one Offline script ended, in the shape the step item needs.</summary>
public sealed record WinPeScriptOutcome(StepItemOutcome Outcome, string? Reason, int? ExitCode, long? DurationMs, string? LastOutputLine);

/// <summary>Runs one Offline script; an interface so Prepare setup can be tested without PowerShell.</summary>
public interface IWinPeScriptRunner
{
    /// <summary>
    /// Runs <paramref name="script"/> from its downloaded <paramref name="textPath"/> (relative to the
    /// applied volume) as file number <paramref name="position"/>, with the applied volume as
    /// <paramref name="windowsDrive"/> (for example <c>W:</c>).
    /// </summary>
    WinPeScriptOutcome Run(SetupScript script, string textPath, int position, string windowsDrive, CancellationToken cancellationToken);
}

/// <summary>
/// Copies the downloaded script (UTF-8 with BOM) to <c>Windows\Temp\CloudImaging\Scripts</c> on the
/// applied volume, not to the WinPE RAM disk, runs it through <see cref="IRestrictedProcessRunner"/>
/// with the applied volume in <c>CI_WINDOWS_DRIVE</c>, and deletes it again. Prepare setup removes
/// the work folder once every script is done.
/// </summary>
public sealed partial class WinPeScriptRunner(IRestrictedProcessRunner processes, ISetupFileSystem volume, Func<bool> supportsPowerShell, ILogger logger) : IWinPeScriptRunner
{
    /// <summary>Our folder on the applied volume, removed by Prepare setup once the scripts are done.</summary>
    public const string WorkFolder = @"Windows\Temp\CloudImaging";

    /// <summary>Where scripts are written, relative to the applied volume's root.</summary>
    public const string ScriptFolder = WorkFolder + @"\Scripts";

    /// <summary>Reason for every script when the boot image has no PowerShell.</summary>
    public const string NoPowerShellReason = "This boot image has no PowerShell, so the script cannot run. Generate a new boot image with Media Builder and update the USB media.";

    /// <summary>Reason when the restart guard could not be set up.</summary>
    public const string GuardUnavailableReason = "Not run because restarts could not be blocked on this device.";

    private const int MaxLastOutputLength = 200;

    /// <inheritdoc/>
    public WinPeScriptOutcome Run(SetupScript script, string textPath, int position, string windowsDrive, CancellationToken cancellationToken)
    {
        if (!supportsPowerShell())
        {
            return new WinPeScriptOutcome(StepItemOutcome.Failed, NoPowerShellReason, null, null, null);
        }

        var relativePath = Path.Combine(ScriptFolder, string.Create(CultureInfo.InvariantCulture, $"{position:0000}.ps1"));
        try
        {
            volume.CopyFile(textPath, relativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WinPeScriptOutcome(StepItemOutcome.Failed, $"The script could not be written to the Windows volume: {ex.Message}", null, null, null);
        }

        try
        {
            var scriptPath = volume.FullPath(relativePath);
            var request = new RestrictedProcessRequest(
                Path.Combine(Environment.SystemDirectory, BootImageCapabilities.PowerShellRelativePath),
                $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                Path.GetDirectoryName(scriptPath)!,
                new Dictionary<string, string> { ["CI_WINDOWS_DRIVE"] = windowsDrive },
                TimeSpan.FromMinutes(script.TimeoutMinutes));

            RestrictedProcessResult result;
            try
            {
                result = processes.Run(request, cancellationToken);
            }
            catch (Win32Exception ex)
            {
                return new WinPeScriptOutcome(StepItemOutcome.Failed, $"The script could not be started: {ex.Message}", null, null, null);
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                var output = string.Join(Environment.NewLine, result.Output);
                LogScriptOutput(logger, script.Name, result.Outcome, result.ExitCode, output);
            }
            var lastLine = result.Output.LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
            if (lastLine is { Length: > MaxLastOutputLength })
            {
                lastLine = lastLine[..MaxLastOutputLength];
            }
            var durationMs = (long)result.Duration.TotalMilliseconds;

            return result.Outcome switch
            {
                RestrictedProcessOutcome.GuardUnavailable => new WinPeScriptOutcome(StepItemOutcome.Failed, GuardUnavailableReason, null, null, null),
                RestrictedProcessOutcome.TimedOut => new WinPeScriptOutcome(StepItemOutcome.Failed, StoppedAfter(script.TimeoutMinutes), null, durationMs, lastLine),
                _ when result.ExitCode == 0 => new WinPeScriptOutcome(StepItemOutcome.Succeeded, null, 0, durationMs, lastLine),
                _ => new WinPeScriptOutcome(StepItemOutcome.Failed, string.Create(CultureInfo.InvariantCulture, $"Exited with code {result.ExitCode}."), result.ExitCode, durationMs, lastLine),
            };
        }
        finally
        {
            try
            {
                volume.DeleteFile(relativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogScriptNotDeleted(logger, relativePath, ex.Message);
            }
        }
    }

    /// <summary>"Stopped after 5 minutes.", or "minute" for one.</summary>
    public static string StoppedAfter(int minutes) =>
        string.Create(CultureInfo.InvariantCulture, $"Stopped after {minutes} {(minutes == 1 ? "minute" : "minutes")}.");

    [LoggerMessage(Level = LogLevel.Information, Message = "Offline script \"{Name}\" ended ({Outcome}, exit code {ExitCode}). Output:\n{Output}")]
    private static partial void LogScriptOutput(ILogger logger, string name, RestrictedProcessOutcome outcome, int? exitCode, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The script file {Path} could not be deleted: {Reason}")]
    private static partial void LogScriptNotDeleted(ILogger logger, string path, string reason);
}
