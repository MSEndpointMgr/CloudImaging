using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Calculates the overall imaging completion percentage from the set of imaging steps (T048, FR-007).
///
/// Calculation rule:
///   Each of the 3 steps (FormatDisk, DownloadImage, ApplyImage) contributes equally (33.33%).
///   A Completed step contributes its full share.
///   An InProgress step contributes a fraction of its share based on StepProgressPercent.
///   Pending and Failed steps contribute 0.
/// </summary>
public static class OverallProgressCalculator
{
    private static readonly ImagingStepName[] Steps =
        [ImagingStepName.FormatDisk, ImagingStepName.DownloadImage, ImagingStepName.ApplyImage];

    private const double SharePerStep = 100.0 / 3.0;

    /// <summary>
    /// Calculates the overall progress percentage (0–100).
    /// Returns 0 if <paramref name="steps"/> is null or empty.
    /// </summary>
    public static int Calculate(IReadOnlyList<ImagingStep>? steps)
    {
        if (steps is null || steps.Count == 0) return 0;

        double total = 0;
        foreach (var stepName in Steps)
        {
            var step = steps.FirstOrDefault(s => s.StepName == stepName);
            if (step is null) continue;

            total += step.Status switch
            {
                ImagingStepStatus.Completed  => SharePerStep,
                ImagingStepStatus.InProgress => SharePerStep * ((step.StepProgressPercent ?? 0) / 100.0),
                _                            => 0,
            };
        }

        return Math.Min(100, (int)Math.Round(total));
    }

    /// <summary>
    /// Returns the name of the currently active (InProgress) step, or null if none is active.
    /// </summary>
    public static string? ActiveStepName(IReadOnlyList<ImagingStep>? steps)
    {
        if (steps is null) return null;
        return steps.FirstOrDefault(s => s.Status == ImagingStepStatus.InProgress)?.StepName.ToString();
    }
}
