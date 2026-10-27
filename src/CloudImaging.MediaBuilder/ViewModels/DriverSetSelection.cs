using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;

namespace CloudImaging.MediaBuilder.ViewModels;

/// <summary>A driver set row in the Driver sets section.</summary>
public sealed class DriverSetRow : INotifyPropertyChanged
{
    private bool _isSelected;

    /// <param name="set">The set.</param>
    public DriverSetRow(MediaBuilderDriverSet set)
    {
        Set = set;
    }

    /// <summary>The set.</summary>
    public MediaBuilderDriverSet Set { get; }

    /// <summary>Model name.</summary>
    public string ModelName => Set.ModelName;

    /// <summary>Windows release name.</summary>
    public string ReleaseName => Set.ReleaseName;

    /// <summary>Size of the set.</summary>
    public string SizeText => DriverSetSelection.FormatSize(Set.Bytes);

    /// <summary>True when the set is copied to the drive.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Driver set rows of one vendor.</summary>
/// <param name="VendorName">Vendor name from the service.</param>
/// <param name="Rows">Rows.</param>
public sealed record DriverSetGroup(string VendorName, IReadOnlyList<DriverSetRow> Rows);

/// <summary>
/// State of the Driver sets section: the sets for the boot image's architecture, the selection,
/// and whether it fits the CACHE partition. Shared files are counted once.
/// </summary>
public sealed class DriverSetSelection : INotifyPropertyChanged
{
    /// <summary>Below this much free space for OS images a warning is shown.</summary>
    public const long OsImageReserveBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>The selection does not fit the drive.</summary>
    public const string DoesNotFitMessage = "The selected driver sets do not fit on this USB drive. Clear some of them or use a larger drive.";

    private IReadOnlyList<DriverSetGroup> _groups = [];
    private MachineArchitecture? _architecture;
    private long? _cacheBytes;
    private bool _isLoading;
    private string? _loadError;

    /// <summary>Rows grouped by vendor.</summary>
    public IReadOnlyList<DriverSetGroup> Groups
    {
        get => _groups;
        private set { _groups = value; Changed(); Changed(nameof(HasSets)); Changed(nameof(EmptyText)); Changed(nameof(HasEmptyText)); }
    }

    /// <summary>True when there are sets to choose from.</summary>
    public bool HasSets => _groups.Count > 0;

    /// <summary>Shown when there is nothing to choose.</summary>
    public string? EmptyText => IsLoading || HasSets || _architecture is null
        ? null
        : LoadError is { } error
            ? "The driver sets could not be loaded. " + error
            : $"No driver sets are available for {(_architecture == MachineArchitecture.Arm64 ? "ARM64" : "x64")} boot images.";

    /// <summary>True when <see cref="EmptyText"/> has a value.</summary>
    public bool HasEmptyText => EmptyText is not null;

    /// <summary>True while the sets load.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; Changed(); Changed(nameof(EmptyText)); Changed(nameof(HasEmptyText)); }
    }

    /// <summary>Why the sets could not be loaded, or null.</summary>
    public string? LoadError
    {
        get => _loadError;
        set { _loadError = value; Changed(); Changed(nameof(EmptyText)); Changed(nameof(HasEmptyText)); }
    }

    /// <summary>Size of the CACHE partition of the selected drive, or null without a drive.</summary>
    public long? CacheBytes
    {
        get => _cacheBytes;
        set { _cacheBytes = value; SelectionChanged(); }
    }

    /// <summary>Selected sets.</summary>
    public IReadOnlyList<MediaBuilderDriverSet> Selected => [.. _groups.SelectMany(g => g.Rows).Where(r => r.IsSelected).Select(r => r.Set)];

    /// <summary>Bytes of the selected sets, shared files once.</summary>
    public long SelectedBytes => Selected.SelectMany(s => s.Files).DistinctBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase).Sum(f => f.SizeBytes);

    /// <summary>Summary under the list, or null when nothing is selected.</summary>
    public string? Summary
    {
        get
        {
            var count = Selected.Count;
            if (count == 0)
            {
                return null;
            }

            var sets = count == 1 ? "1 driver set" : $"{count} driver sets";
            var text = $"{sets}, {FormatSize(SelectedBytes)}.";
            return CacheBytes is { } cache && !DoesNotFit ? $"{text} {FormatSize(cache - SelectedBytes)} stays free for OS images." : text;
        }
    }

    /// <summary>True when less than 20 GB would stay free for OS images.</summary>
    public bool ShowLowSpaceWarning => Selected.Count > 0 && CacheBytes is { } cache && !DoesNotFit && cache - SelectedBytes < OsImageReserveBytes;

    /// <summary>True when the selection does not fit the CACHE partition; Prepare is disabled.</summary>
    public bool DoesNotFit => CacheBytes is { } cache && SelectedBytes > cache;

    /// <summary>Replaces the sets with those for an architecture. Nothing is selected.</summary>
    /// <param name="architecture">Boot image architecture.</param>
    /// <param name="sets">Sets from the service.</param>
    public void Load(MachineArchitecture architecture, IReadOnlyList<MediaBuilderDriverSet> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        foreach (var row in _groups.SelectMany(g => g.Rows))
        {
            row.PropertyChanged -= OnRowChanged;
        }

        _architecture = architecture;
        var groups = sets
            .GroupBy(s => s.VendorName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DriverSetGroup(g.Key, [.. g.Select(s => new DriverSetRow(s))]))
            .ToList();
        foreach (var row in groups.SelectMany(g => g.Rows))
        {
            row.PropertyChanged += OnRowChanged;
        }

        Groups = groups;
        SelectionChanged();
    }

    /// <summary>Clears the sets (no boot image).</summary>
    public void Clear()
    {
        _architecture = null;
        Load(MachineArchitecture.X64, []);
        _architecture = null;
        Changed(nameof(EmptyText));
        Changed(nameof(HasEmptyText));
    }

    /// <summary>Formats a size as "4.8 GB".</summary>
    /// <param name="bytes">Bytes.</param>
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{size:0.0} {units[unit]}");
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => SelectionChanged();

    private void SelectionChanged()
    {
        Changed(nameof(Selected));
        Changed(nameof(SelectedBytes));
        Changed(nameof(Summary));
        Changed(nameof(ShowLowSpaceWarning));
        Changed(nameof(DoesNotFit));
        Changed(nameof(CacheBytes));
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
