using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>An individual imaging step within a session (FR-007, Key Entities).</summary>
public sealed class ImagingStep
{
    public required ImagingStepName StepName { get; init; }
    public required ImagingStepStatus Status { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ErrorDetail { get; init; }

    /// <summary>
    /// Optional sub-progress (0-100) for steps that can report internal progress
    /// (e.g., download-image byte transfer). Does not contribute fractional value
    /// to overallProgressPercent until the step completes (FR-007).
    /// </summary>
    public int? StepProgressPercent { get; init; }
}
