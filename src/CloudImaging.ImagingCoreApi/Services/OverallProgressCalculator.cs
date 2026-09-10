using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Calculates the overall imaging completion percentage from the set of imaging steps (T048, FR-007).
///
/// Calculation rule:
///   The pipeline the Client actually runs has 5 steps (FormatDisk, DownloadImage, ApplyImage,
///   ConfigureBoot, ApplyRecoveryImage) — see CloudImaging.Client's ImagingWorkflowViewModel /
///   ProgressViewModel. Each step carries the same weight it does in the Client's own on-device
///   progress ring, so the portal and the device agree on the number rather than each telling the
///   technician a different story.
///   A Completed step contributes its full share.
///   An InProgress step contributes a fraction of its share based on StepProgressPercent.
///   Pending and Failed steps contribute 0.
/// </summary>
public static class OverallProgressCalculator
{
    /// <summary>
    /// Per-step weight, summing to 100. Mirrors the band each step occupies in the Client's
    /// <c>ImagingWorkflowViewModel</c> (0–8 format, 8–55 download, 55–80 apply, 80–85 boot config,
    /// 85–100 recovery) so both surfaces report the same percentage for the same device.
    /// Declared in pipeline order — <see cref="PipelineStepCount"/> is the authoritative count of
    /// steps a session must complete before it is considered done.
    /// </summary>
    private static readonly (ImagingStepName Name, double Share)[] Steps =
    [
        (ImagingStepName.FormatDisk, 8),
        (ImagingStepName.DownloadImage, 47),
        (ImagingStepName.ApplyImage, 25),
        (ImagingStepName.ConfigureBoot, 5),
        (ImagingStepName.ApplyRecoveryImage, 15),
    ];

    /// <summary>
    /// Number of steps in the imaging pipeline. Callers use this to decide when a session has
    /// completed every step, instead of hard-coding a count that silently goes stale whenever a
    /// step is added to <see cref="ImagingStepName"/>.
    /// </summary>
    public static int PipelineStepCount => Steps.Length;

    /// <summary>
    /// Calculates the overall progress percentage (0–100).
    /// Returns 0 if <paramref name="steps"/> is null or empty.
    /// </summary>
    public static int Calculate(IReadOnlyList<ImagingStep>? steps)
    {
        if (steps is null || steps.Count == 0)
        {
            return 0;
        }

        double total = 0;
        foreach (var (stepName, share) in Steps)
        {
            var step = steps.FirstOrDefault(s => s.StepName == stepName);
            if (step is null)
            {
                continue;
            }

            total += step.Status switch
            {
                ImagingStepStatus.Completed => share,
                ImagingStepStatus.InProgress => share * ((step.StepProgressPercent ?? 0) / 100.0),
                _ => 0,
            };
        }

        return Math.Min(100, (int)Math.Round(total));
    }

    /// <summary>
    /// Returns the step the session is currently on, or — once nothing is running any more — the
    /// last step it reached. The portal surfaces this as "Step" while a session is being monitored
    /// and as "Last step" once it has failed, so returning null the moment the active step stops
    /// being InProgress would blank the column exactly when a technician needs it most (a failed
    /// session has no InProgress step, only a Failed one).
    ///
    /// Precedence: the InProgress step, else the Failed step (where the session stopped), else the
    /// furthest step that completed. Null only when no step has been reported at all.
    /// </summary>
    public static string? CurrentOrLastStepName(IReadOnlyList<ImagingStep>? steps)
    {
        if (steps is null || steps.Count == 0)
        {
            return null;
        }

        var active = steps.FirstOrDefault(s => s.Status == ImagingStepStatus.InProgress);
        if (active is not null)
        {
            return active.StepName.ToString();
        }

        var failed = steps.FirstOrDefault(s => s.Status == ImagingStepStatus.Failed);
        if (failed is not null)
        {
            return failed.StepName.ToString();
        }

        // Walk the pipeline backwards so the furthest completed step wins, regardless of the
        // (unordered) order Table Storage returned the step rows in.
        for (var i = Steps.Length - 1; i >= 0; i--)
        {
            var name = Steps[i].Name;
            if (steps.Any(s => s.StepName == name && s.Status == ImagingStepStatus.Completed))
            {
                return name.ToString();
            }
        }

        return null;
    }
}
