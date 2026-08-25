using CloudImaging.ImagingCoreApi.Functions;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="BrandingFunctions.NormalizeLogoImage"/> (FR-038): uploaded boot
/// image logos are resized server-side so the Cloud Imaging Client never has to upscale a tiny
/// source image at render time in WinPE, which was producing a grainy/aliased logo.
/// </summary>
public sealed class BrandingLogoNormalizationTests
{
    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0, 120, 212, 255));
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    [Fact]
    public void SmallRasterLogo_IsUpscaled_ToCanonicalMaxDimension()
    {
        var small = CreatePng(16, 16);

        var (bytes, contentType, error) = BrandingFunctions.NormalizeLogoImage(small, "image/png");

        error.Should().BeEmpty();
        contentType.Should().Be("image/png");
        using var result = SKBitmap.Decode(bytes);
        Math.Max(result.Width, result.Height).Should().Be(BrandingFunctions.LogoCanonicalMaxDimension);
    }

    [Fact]
    public void LargeRasterLogo_IsDownscaled_ToCanonicalMaxDimension()
    {
        var large = CreatePng(1000, 400);

        var (bytes, contentType, error) = BrandingFunctions.NormalizeLogoImage(large, "image/png");

        error.Should().BeEmpty();
        contentType.Should().Be("image/png");
        using var result = SKBitmap.Decode(bytes);
        result.Width.Should().Be(BrandingFunctions.LogoCanonicalMaxDimension);
        result.Height.Should().Be(102); // proportional: round(400 * (256/1000))
    }

    [Fact]
    public void RasterLogo_PreservesAspectRatio_AfterResize()
    {
        var wide = CreatePng(500, 250);

        var (bytes, _, _) = BrandingFunctions.NormalizeLogoImage(wide, "image/png");

        using var result = SKBitmap.Decode(bytes);
        result.Width.Should().Be(BrandingFunctions.LogoCanonicalMaxDimension);
        result.Height.Should().Be(BrandingFunctions.LogoCanonicalMaxDimension / 2);
    }

    [Fact]
    public void SvgLogo_IsReturnedUnchanged()
    {
        var svgBytes = "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray();

        var (bytes, contentType, error) = BrandingFunctions.NormalizeLogoImage(svgBytes, "image/svg+xml");

        error.Should().BeEmpty();
        contentType.Should().Be("image/svg+xml");
        bytes.Should().BeSameAs(svgBytes);
    }

    [Fact]
    public void UndecodableBytes_AreReturnedUnchanged_RatherThanRejected()
    {
        var garbage = new byte[] { 1, 2, 3, 4, 5 };

        var (bytes, contentType, error) = BrandingFunctions.NormalizeLogoImage(garbage, "image/png");

        error.Should().BeEmpty();
        contentType.Should().Be("image/png");
        bytes.Should().BeSameAs(garbage);
    }

    [Fact]
    public void AlreadyCanonicalSize_IsNotReEncoded()
    {
        var exact = CreatePng(BrandingFunctions.LogoCanonicalMaxDimension, BrandingFunctions.LogoCanonicalMaxDimension);

        var (bytes, _, error) = BrandingFunctions.NormalizeLogoImage(exact, "image/png");

        error.Should().BeEmpty();
        bytes.Should().BeSameAs(exact);
    }

    [Fact]
    public void OversizedDecodedImage_ReturnsError()
    {
        var huge = CreatePng(BrandingFunctions.LogoMaxDecodedDimension + 1, 10);

        var (_, _, error) = BrandingFunctions.NormalizeLogoImage(huge, "image/png");

        error.Should().NotBeEmpty();
    }
}
