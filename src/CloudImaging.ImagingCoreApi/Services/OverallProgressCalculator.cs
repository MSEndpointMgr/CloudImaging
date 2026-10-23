using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Calculates the overall imaging completion percentage from the set of imaging steps (T048, FR-007).
///
/// Calculation rule:
///   The pipeline the Client actually runs has 5 required steps (FormatDisk, DownloadImage,
///   ApplyImage, ConfigureBoot, ApplyRecoveryImage) and optional steps (PrepareSetup) that only
///   run when the device has work for them; see CloudImaging.Client's ImagingWorkflowViewModel /
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
    /// Every step in the order the Client runs them, with its weight (summing to 100) and whether
    /// a session must report it to complete. Weights mirror the band each required step occupies in
    /// the Client's <c>ImagingWorkflowViewModel</c> (0-8 format, 8-55 download, 55-80 apply, 80-85
    /// boot config, 85-100 recovery). Optional steps have weight 0 so the overall percentage holds
    /// while they run, and count only when the device reports them.
    /// </summary>
    private static readonly (ImagingStepName Name, double Share, bool Required)[] Steps =
    [
        (ImagingStepName.FormatDisk, 8, true),
        (ImagingStepName.DownloadImage, 47, true),
        (ImagingStepName.ApplyImage, 25, true),
        (ImagingStepName.PrepareSetup, 0, false),
        (ImagingStepName.ConfigureBoot, 5, true),
        (ImagingStepName.ApplyRecoveryImage, 15, true),
    ];

    /// <summary>Every step name in the order the Client runs them, required and optional.</summary>
    public static IReadOnlyList<ImagingStepName> PipelineOrder { get; } = [.. Steps.Select(step => step.Name)];

    /// <summary>
    /// True when every required step is Completed and no reported step is InProgress or Failed.
    /// Optional steps a device skips are never reported, so they do not hold a session open.
    /// </summary>
    public static bool IsPipelineComplete(IReadOnlyList<ImagingStep>? steps)
    {
        if (steps is null || steps.Count == 0)
        {
            return false;
        }

        if (steps.Any(step => step.Status is ImagingStepStatus.InProgress or ImagingStepStatus.Failed))
        {
            return false;
        }

        return Steps
            .Where(step => step.Required)
            .All(required => steps.Any(step => step.StepName == required.Name && step.Status == ImagingStepStatus.Completed));
    }

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
        foreach (var (stepName, share, _) in Steps)
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
