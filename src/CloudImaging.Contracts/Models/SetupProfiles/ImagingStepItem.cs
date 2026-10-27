using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.SetupProfiles;

/// <summary>The outcome of one item of an imaging step, such as a registry value or a script.</summary>
public sealed record ImagingStepItem
{
    /// <summary>Most items kept per step report.</summary>
    public const int MaxItemsPerReport = 1500;

    /// <summary>Longest kept <see cref="Name"/>.</summary>
    public const int MaxNameLength = 100;

    /// <summary>Longest kept <see cref="Target"/>.</summary>
    public const int MaxTargetLength = 300;

    /// <summary>Longest kept <see cref="Reason"/>.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>Longest kept <see cref="LastOutputLine"/>.</summary>
    public const int MaxLastOutputLineLength = 200;

    /// <summary>Position reported by the device.</summary>
    public int Index { get; init; }

    /// <summary>Item kind.</summary>
    public StepItemKind Kind { get; init; }

    /// <summary>Registry value name, script name, "Answer file" or driver component name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>What the item acted on, for example the full registry path.</summary>
    public string? Target { get; init; }

    /// <summary>Outcome.</summary>
    public StepItemOutcome Outcome { get; init; }

    /// <summary>Why the item failed or did not run.</summary>
    public string? Reason { get; init; }

    /// <summary>Script exit code.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Script duration in milliseconds.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Last non-empty output line of an In WinPE script.</summary>
    public string? LastOutputLine { get; init; }

    /// <summary>Action of a driver component; null for other items.</summary>
    public DriverAction? DriverAction { get; init; }

    /// <summary>Version of a driver component.</summary>
    public string? ComponentVersion { get; init; }

    /// <summary>What kind of driver component the device got.</summary>
    public DriverComponentOrigin? DriverOrigin { get; init; }

    /// <summary>True when the driver file came from the USB drive instead of a download.</summary>
    public bool? FromUsbCache { get; init; }
}

/// <summary>Number of items of one kind with one outcome.</summary>
public sealed record ImagingStepItemCount
{
    /// <summary>Item kind.</summary>
    public StepItemKind Kind { get; init; }

    /// <summary>Outcome.</summary>
    public StepItemOutcome Outcome { get; init; }

    /// <summary>Number of items.</summary>
    public int Count { get; init; }

    /// <summary>Items whose file came from the USB drive; null when none did.</summary>
    public int? FromUsbCache { get; init; }
}
