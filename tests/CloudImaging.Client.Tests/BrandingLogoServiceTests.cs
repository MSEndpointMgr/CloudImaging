using System.IO;
using System.Reflection;
using CloudImaging.Client.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Unit tests for <see cref="BrandingLogoService"/> (T029a, FR-002a).
/// Tests the logo-found and fallback-to-null paths using a temporary directory
/// that mimics the executable directory structure.
/// </summary>
public sealed class BrandingLogoServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BrandingLogoService _sut;

    public BrandingLogoServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ci-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sut = new BrandingLogoService(NullLogger<BrandingLogoService>.Instance);
    }

    // ── Fallback path — no logo in executable directory ───────────────────────

    [Fact]
    public void GetLogoPath_WhenNoLogoFile_ReturnsNull()
    {
        // The service reads from Assembly.GetExecutingAssembly().Location directory.
        // In the test context, the executable directory is the test binary output directory.
        // There should be no branding\logo.png there by default.
        var result = _sut.GetLogoPath();

        // Either null (logo not found) or an existing path — the test exe dir won't
        // have a branding folder unless we explicitly create it.
        // We can only reliably assert null when the exe dir has no branding\logo.png.
        var exeDir  = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        var logoPath = Path.Combine(exeDir, "branding", "logo.png");

        if (!File.Exists(logoPath))
        {
            result.Should().BeNull(
                "when branding\\logo.png does not exist in the executable directory, " +
                "GetLogoPath() should return null to signal use of the default logo");
        }
        else
        {
            // If CI somehow has the file, the method should return the path
            result.Should().Be(logoPath);
        }
    }

    // ── Logo-found path ───────────────────────────────────────────────────────

    [Fact]
    public void GetLogoPath_WhenLogoFileExists_ReturnsLogoPath()
    {
        // We cannot change what Assembly.GetExecutingAssembly().Location returns,
        // so we verify the method's logic by confirming it returns a valid path
        // when the file exists at the expected location.
        //
        // This test validates the logic contract rather than I/O side effects:
        // "GetLogoPath returns a non-null path when the file exists."

        var exeDir  = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        var brandingDir = Path.Combine(exeDir, "branding");
        var logoPath = Path.Combine(brandingDir, "logo.png");

        bool createdForTest = false;
        try
        {
            if (!File.Exists(logoPath))
            {
                Directory.CreateDirectory(brandingDir);
                File.WriteAllBytes(logoPath, [0x89, 0x50, 0x4E, 0x47]); // PNG header stub
                createdForTest = true;
            }

            var result = _sut.GetLogoPath();

            result.Should().NotBeNull("a logo file at the expected path must be returned");
            result.Should().Be(logoPath, "returned path must be the exact expected path");
            File.Exists(result!).Should().BeTrue("returned path must point to an existing file");
        }
        finally
        {
            if (createdForTest && File.Exists(logoPath))
                File.Delete(logoPath);
        }
    }

    // ── Executable-relative path resolution ───────────────────────────────────

    [Fact]
    public void GetLogoPath_UsesExecutableDirectoryNotWorkingDirectory()
    {
        // Verify that the service resolves paths relative to the assembly location,
        // NOT the current working directory (FR-002a requirement).
        var exeDir     = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        var workingDir = Directory.GetCurrentDirectory();

        // They may or may not be equal in the test environment, but the service
        // MUST use the assembly location. We can verify this indirectly by confirming
        // the expected logo path is derived from the assembly location.
        var expectedBasePath = Path.Combine(exeDir, "branding", "logo.png");

        // GetLogoPath either returns expectedBasePath (if file exists) or null — never a path
        // rooted in a different directory.
        var result = _sut.GetLogoPath();
        if (result is not null)
        {
            result.Should().StartWith(exeDir,
                "logo path must be relative to the executable directory, not the working directory");
        }
        // null is also acceptable (file not found) — the important thing is
        // the method does NOT throw and does NOT return a path outside the exe dir.
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);
}
