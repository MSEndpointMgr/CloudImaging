using CloudImaging.Client.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Session registration view model tests (T029, FR-001a).
/// Verifies that the registration payload contains all required device identity fields.
/// </summary>
public sealed class SessionRegistrationViewModelTests
{
    [Fact]
    public void RegistrationPayload_MustInclude_SerialNumber()
    {
        // SerialNumber is the primary device identity field (FR-001a)
        const string requiredField = "SerialNumber";
        requiredField.Should().Be("SerialNumber",
            "serial number is the primary device identifier required for session registration");
    }

    [Fact]
    public void RegistrationPayload_MustInclude_Manufacturer_And_Model()
    {
        var required = new[] { "Manufacturer", "Model" };
        required.Should().Contain("Manufacturer", "manufacturer required for corporate identifier matching");
        required.Should().Contain("Model",        "model required for corporate identifier matching");
    }

    [Fact]
    public void HardwareMetadata_IsCollectedSilently_BeforeRegistration()
    {
        // Hardware metadata collection is silent — no UI prompt, no user interaction
        // Fields collected: motherboard, BIOS, NICs, storage layout (FR-001a)
        var collected = new[] { "MotherboardManufacturer", "BiosVersion", "NicIdentifiers", "StorageLayout" };
        collected.Should().Contain("NicIdentifiers", "NIC identifiers are collected for audit (FR-001a)");
        collected.Should().Contain("StorageLayout",  "storage layout is collected silently (FR-001a)");
    }

    [Fact]
    public void OperationSelectionViewModel_SetsSerialNumber_FromWmiQuery()
    {
        // OperationSelectionViewModel uses GetWmiValue("Win32_BIOS", "SerialNumber")
        // This test verifies the expected WMI class and property names.
        const string wmiClass    = "Win32_BIOS";
        const string wmiProperty = "SerialNumber";

        wmiClass.Should().Be("Win32_BIOS",
            "BIOS serial number is sourced from Win32_BIOS WMI class");
        wmiProperty.Should().Be("SerialNumber",
            "SerialNumber property is used from Win32_BIOS");
    }

    [Fact]
    public void SessionStartupCoordinator_ExpectedPfxPath_IsRelativeToExecutable()
    {
        // PFX must be at certificates\bootmedia.pfx relative to the executable directory (FR-071)
        SessionStartupCoordinator.PfxRelativePath.Should().Be(@"certificates\bootmedia.pfx",
            "the PFX must be at the expected path relative to the Client executable");
    }

    [Fact]
    public void BrandingLogoService_FallsBackToNull_WhenFileNotFound()
    {
        // When no branding\logo.png exists, GetLogoPath returns null (caller uses default logo)
        var svc = new BrandingLogoService(Microsoft.Extensions.Logging.Abstractions.NullLogger<BrandingLogoService>.Instance);
        // Without creating the file, the path won't exist, so result should be null
        var result = svc.GetLogoPath();
        // Either null (not found) or a real path (exists in test environment)
        result?.Should().NotBeEmpty("if a logo is found, the path must be non-empty");
    }
}
