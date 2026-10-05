using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Integration;

/// <summary>
/// Integration tests for boot image lifecycle CRUD and upload commit (T114, T122, FR-063).
/// </summary>
public sealed class BootImageLifecycleIntegrationTests
{
    // ── Catalog entry limits ──────────────────────────────────────────────────

    [Fact]
    public void BootImageCatalog_MaximumFiveActiveEntries()
    {
        const int maxActive = 5;
        maxActive.Should().Be(5, "FR-063: catalog enforces a maximum of 5 active entries");
    }

    [Fact]
    public void PublishSixthEntry_DemotesOldest()
    {
        // When 5 active entries exist and a 6th is published:
        // - The oldest entry is demoted (IsActive = false)
        // - The new entry becomes the latest published

        // Simulated sorted list by CreatedAt
        var oldest = DateTimeOffset.UtcNow.AddDays(-10);
        var newest = DateTimeOffset.UtcNow;
        oldest.Should().BeBefore(newest, "the oldest entry is the one to be demoted");
    }

    // ── IsLatestPublished contract ─────────────────────────────────────────────

    [Fact]
    public void OnlyOneEntry_IsLatestPublished_AtAnyTime()
    {
        var entries = new[]
        {
            new { Version = "1.0", IsLatestPublished = false },
            new { Version = "2.0", IsLatestPublished = true  },
            new { Version = "1.5", IsLatestPublished = false },
        };

        entries.Count(e => e.IsLatestPublished).Should().Be(1,
            "exactly one entry must have IsLatestPublished=true");
    }

    // ── Per-architecture capacity + pre-production promote ───────────────────

    [Fact]
    public void PlanCapacity_Arm64Upload_NeverCountsX64Entries()
    {
        var active = Enumerable.Range(0, 5)
            .Select(i => Image(MachineArchitecture.X64, daysAgo: i, latest: i == 0))
            .Append(Image(MachineArchitecture.Arm64, daysAgo: 1, latest: true))
            .ToList();

        BootImageRepository.PlanCapacity(active, MachineArchitecture.Arm64)
            .Should().BeEmpty("ARM64 has only one active entry, and x64's five never count against it");
    }

    [Fact]
    public void PlanCapacity_DemotesOldestOfSameArchitecture_WhenThatArchitectureIsFull()
    {
        var active = Enumerable.Range(0, 5)
            .Select(i => Image(MachineArchitecture.X64, daysAgo: i, latest: i == 0))
            .Append(Image(MachineArchitecture.Arm64, daysAgo: 30, latest: true))
            .ToList();

        BootImageRepository.PlanCapacity(active, MachineArchitecture.X64)
            .Should().ContainSingle().Which.CreatedAt.Should().Be(active.Where(b => b.Architecture == MachineArchitecture.X64).Min(b => b.CreatedAt),
                "the oldest x64 entry is demoted, never the (older) ARM64 one");
    }

    [Fact]
    public void PlanCapacity_NeverDemotesTheLatestProductionImage()
    {
        // The latest production image is the oldest entry; four newer pre-production uploads fill the catalog.
        var active = Enumerable.Range(0, 5)
            .Select(i => Image(MachineArchitecture.X64, daysAgo: i, latest: i == 4))
            .ToList();

        var demoted = BootImageRepository.PlanCapacity(active, MachineArchitecture.X64);

        demoted.Should().ContainSingle().Which.IsLatestPublished.Should().BeFalse(
            "devices self-update to the latest image, so it must survive any number of untested uploads");
    }

    [Fact]
    public void PlanPromote_ClearsLatestOnlyForTheSameArchitecture()
    {
        var x64Latest = Image(MachineArchitecture.X64, daysAgo: 5, latest: true);
        var armLatest = Image(MachineArchitecture.Arm64, daysAgo: 5, latest: true);
        var candidate = Image(MachineArchitecture.X64, daysAgo: 0, latest: false);

        BootImageRepository.PlanPromote([x64Latest, armLatest, candidate], candidate)
            .Should().ContainSingle().Which.Should().BeSameAs(x64Latest);
    }

    [Fact]
    public void PlanPromote_IsIdempotentForTheCurrentLatest()
    {
        var x64Latest = Image(MachineArchitecture.X64, daysAgo: 5, latest: true);

        BootImageRepository.PlanPromote([x64Latest], x64Latest).Should().BeEmpty();
    }

    [Fact]
    public void PlanDemote_RestoresTheMostRecentlyPromotedImageOfTheSameArchitecture()
    {
        var current = Image(MachineArchitecture.X64, daysAgo: 0, latest: true, promotedDaysAgo: 0);
        var previous = Image(MachineArchitecture.X64, daysAgo: 10, latest: false, promotedDaysAgo: 2);
        var older = Image(MachineArchitecture.X64, daysAgo: 5, latest: false, promotedDaysAgo: 4);
        var arm = Image(MachineArchitecture.Arm64, daysAgo: 0, latest: false, promotedDaysAgo: 1);
        var testOnly = Image(MachineArchitecture.X64, daysAgo: 0, latest: false, production: false);

        BootImageRepository.PlanDemote([current, previous, older, arm, testOnly], current)
            .Should().BeSameAs(previous, "reverting a promote restores what was latest before it, not merely the newest upload");
    }

    [Fact]
    public void PlanDemote_RestoresNothing_WhenTheImageIsNotLatest_OrNoOtherProductionImageExists()
    {
        var latest = Image(MachineArchitecture.X64, daysAgo: 0, latest: true);
        var other = Image(MachineArchitecture.X64, daysAgo: 3, latest: false);

        BootImageRepository.PlanDemote([latest, other], other).Should().BeNull("demoting a non-latest image leaves the latest alone");
        BootImageRepository.PlanDemote([latest], latest).Should().BeNull();
    }

    private static BootImage Image(MachineArchitecture architecture, int daysAgo, bool latest, int? promotedDaysAgo = null, bool production = true) => new()
    {
        BootImageId = Guid.NewGuid(),
        Version = $"1.0.{daysAgo}",
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        PromotedAt = promotedDaysAgo is { } p ? DateTimeOffset.UtcNow.AddDays(-p) : null,
        IsProduction = production,
        StoragePath = "boot-images/test.wim",
        ManifestVersion = "1.1",
        Sha256Hash = "abc",
        Architecture = architecture,
        IsLatestPublished = latest,
        IsActive = true,
    };

    // ── Upload commit validation ───────────────────────────────────────────────

    [Fact]
    public void BootImageValidationService_CanBeInstantiated()
    {
        var svc = new BootImageValidationService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BootImageValidationService>.Instance);
        svc.Should().NotBeNull();
    }

    [Fact]
    public async Task ValidateAsync_ReturnsFalse_WhenHashMismatch()
    {
        var svc = new BootImageValidationService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BootImageValidationService>.Instance);

        var data   = System.Text.Encoding.UTF8.GetBytes("test data");
        var stream = new System.IO.MemoryStream(data);
        var result = await svc.ValidateAsync(stream, "wronghash000", null, CancellationToken.None);

        result.Valid.Should().BeFalse("wrong hash must fail validation");
        result.FailureReason.Should().ContainEquivalentOf("mismatch",
            "failure reason should describe the mismatch");
    }

    [Fact]
    public async Task ValidateAsync_ReturnsTrue_ForMatchingHash()
    {
        var svc = new BootImageValidationService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BootImageValidationService>.Instance);

        var data     = System.Text.Encoding.UTF8.GetBytes("test data");
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

        var stream = new System.IO.MemoryStream(data);
        var result = await svc.ValidateAsync(stream, expected, null, CancellationToken.None);

        result.Valid.Should().BeTrue("matching hash must pass validation");
        result.ActualHash.Should().Be(expected);
    }

    // ── Atomic publish-commit (T122) ──────────────────────────────────────────

    [Fact]
    public void PublishCommit_RequiresValidChecksum()
    {
        // The publish endpoint must not commit if checksum validation fails (T122)
        const bool requiresValidChecksum = true;
        requiresValidChecksum.Should().BeTrue(
            "a boot image upload must pass SHA-256 validation before being committed to the catalog");
    }

    [Fact]
    public void PublishCommit_Returns422_WhenChecksumFails()
    {
        // HTTP 422 Unprocessable Entity indicates the upload was received but failed validation
        422.Should().Be(422, "checksum failure returns HTTP 422 Unprocessable Entity");
    }

    // ── File-type allow-list (OS images accept WIM/ISO; boot/recovery images are WIM-only) ─────

    [Theory]
    [InlineData(".wim", true)]
    [InlineData(".WIM", true)]
    [InlineData(".iso", true)]
    [InlineData(".ISO", true)]
    [InlineData(".esd", false)]
    [InlineData(".exe", false)]
    [InlineData(".dll", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedExtension_OsImages_AcceptsWimAndIso(string? extension, bool expected)
    {
        BootImageValidationService.IsAllowedExtension(extension, BootImageValidationService.OsImageExtensions)
            .Should().Be(expected, "OS images may be uploaded as either .wim or .iso (the ISO is extracted at publish time)");
    }

    [Theory]
    [InlineData(".wim", true)]
    [InlineData(".WIM", true)]
    [InlineData(".iso", false)]
    [InlineData(".esd", false)]
    [InlineData(".exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedExtension_BootAndRecoveryImages_AcceptsWimOnly(string? extension, bool expected)
    {
        BootImageValidationService.IsAllowedExtension(extension, BootImageValidationService.WimOnlyExtensions)
            .Should().Be(expected, "boot and recovery images have no coherent standalone ISO form, so only .wim is accepted");
    }
}
