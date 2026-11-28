using CloudImaging.ImagingCoreApi.Services.Drivers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudImaging.ImagingCoreApi.Tests.TestDoubles;

/// <summary>Microsoft's feature update catalog as a test sets it, counting the reads.</summary>
internal sealed class FakeWindowsReleaseSource : IWindowsReleaseSource
{
    /// <summary>What the next read returns; no releases by default.</summary>
    public WindowsReleaseRead Next { get; set; } = new([], null);

    public int Reads { get; private set; }

    /// <summary>A release catalog over the test bed's tables and clock.</summary>
    public static WindowsReleaseCatalogService Catalog(DriverTestBed bed, FakeWindowsReleaseSource? source = null) =>
        new(bed.Store, source ?? new FakeWindowsReleaseSource(), bed.Time, NullLogger<WindowsReleaseCatalogService>.Instance);

    public Task<WindowsReleaseRead> ReadAsync(CancellationToken ct = default)
    {
        Reads++;
        return Task.FromResult(Next);
    }
}
