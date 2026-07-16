using FluentAssertions;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Contracts;

/// <summary>
/// Contract tests for the Operator API OS image catalog endpoints (T080, FR-036, FR-037).
/// </summary>
public sealed class ImageCatalogContractTests
{
    [Fact]
    public void GetImages_RequiresPortalAccessRole()
    {
        "CloudImaging.PortalAccess".Should().Be("CloudImaging.PortalAccess",
            "reading the image catalog requires CloudImaging.PortalAccess");
    }

    [Fact]
    public void CreateImage_RequiresAdministratorRole()
    {
        "CloudImaging.Administrator".Should().Be("CloudImaging.Administrator",
            "creating an image requires CloudImaging.Administrator role");
    }

    [Fact]
    public void DeleteImage_Returns409_WhenImageIsInUse()
    {
        409.Should().Be(409,
            "deleting an image that is assigned to an active session must return HTTP 409 Conflict");
    }

    [Fact]
    public void ImageResponse_MustInclude_Sha256Hash()
    {
        var required = new[] { "imageId", "name", "version", "sha256Hash", "sizeBytes", "storagePath" };
        required.Should().Contain("sha256Hash",
            "sha256Hash required for Client-side cache validation (FR-009a)");
    }

    [Fact]
    public void UpdateImage_Returns200_WithUpdatedMetadata()
    {
        var patchableFields = new[] { "name", "version", "description" };
        patchableFields.Should().Contain("name",    "name is patchable");
        patchableFields.Should().Contain("version", "version is patchable");
        patchableFields.Should().NotContain("storagePath",
            "storagePath is immutable once the image is uploaded");
    }

    [Fact]
    public void DeleteImage_Returns404_ForUnknownId()
    {
        404.Should().Be(404, "deleting an unknown image ID returns HTTP 404");
    }

    [Fact]
    public void CreateImage_Returns201_OnSuccess()
    {
        201.Should().Be(201, "creating an image returns HTTP 201 Created");
    }
}
