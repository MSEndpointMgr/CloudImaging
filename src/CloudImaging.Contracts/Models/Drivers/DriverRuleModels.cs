using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.Drivers;

/// <summary>A driver rule.</summary>
public sealed record DriverRule
{
    /// <summary>Rule id.</summary>
    public Guid RuleId { get; init; }

    /// <summary>Name, 1 to 100 characters, unique ignoring case.</summary>
    public required string Name { get; init; }

    /// <summary>Vendors the rule applies to.</summary>
    public DriverRuleScope Scope { get; init; }

    /// <summary>What the rule matches on.</summary>
    public DriverRuleMatch Match { get; init; }

    /// <summary>Category, name pattern or component id.</summary>
    public required string Value { get; init; }

    /// <summary>Action for matching components.</summary>
    public DriverAction Action { get; init; }

    /// <summary>When the rule last changed.</summary>
    public DateTimeOffset ChangedAt { get; init; }

    /// <summary>Who changed it.</summary>
    public string? ChangedBy { get; init; }

    /// <summary>Components the rule decides in the current sets.</summary>
    public int Hits { get; init; }
}

/// <summary>Request to create or update a rule.</summary>
public sealed record DriverRuleRequest
{
    /// <summary>Name.</summary>
    public string? Name { get; init; }

    /// <summary>Scope.</summary>
    public DriverRuleScope Scope { get; init; }

    /// <summary>Match kind.</summary>
    public DriverRuleMatch Match { get; init; }

    /// <summary>Value.</summary>
    public string? Value { get; init; }

    /// <summary>Action.</summary>
    public DriverAction Action { get; init; }

    /// <summary>Who made the change.</summary>
    public DriverActor? Actor { get; init; }
}

/// <summary>A vendor category and whether a failure in it fails the deployment.</summary>
public sealed record DriverCategory
{
    /// <summary>Vendor.</summary>
    public DriverVendor Vendor { get; init; }

    /// <summary>Category name.</summary>
    public required string Category { get; init; }

    /// <summary>True when a failed component of this category fails the deployment.</summary>
    public bool Critical { get; init; }
}

/// <summary>Request to replace a vendor's critical categories.</summary>
public sealed record DriverCriticalCategoriesRequest
{
    /// <summary>Critical categories.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Who made the change.</summary>
    public DriverActor? Actor { get; init; }
}
