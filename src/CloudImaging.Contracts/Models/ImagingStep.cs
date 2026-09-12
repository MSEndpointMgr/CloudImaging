using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>An individual imaging step within a session (FR-007, Key Entities).</summary>
public sealed class ImagingStep
{
    /// <summary>Which imaging step this entry describes (FR-007).</summary>
    public required ImagingStepName StepName { get; init; }

    /// <summary>Current execution status of the step.</summary>
    public required ImagingStepStatus Status { get; init; }

    /// <summary>UTC timestamp when the step started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>UTC timestamp when the step completed.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Operator-facing error detail when the step failed.</summary>
    public string? ErrorDetail { get; init; }

    /// <summary>
    /// Optional sub-progress (0-100) for steps that can report internal progress
    /// (e.g., download-image byte transfer). Does not contribute fractional value
    /// to overallProgressPercent until the step completes (FR-007).
    /// </summary>
    public int? StepProgressPercent { get; init; }
}
