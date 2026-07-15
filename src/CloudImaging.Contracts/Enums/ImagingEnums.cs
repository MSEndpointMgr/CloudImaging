namespace CloudImaging.Contracts.Enums;

/// <summary>Machine-readable imaging step identifiers used in API payloads (FR-007).</summary>
public enum ImagingStepName
{
    FormatDisk,
    DownloadImage,
    ApplyImage
}

/// <summary>Step execution states.</summary>
public enum ImagingStepStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
}
