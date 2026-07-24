using SkiaSharp;
using TicketFlow.Functions;

namespace TicketFlow.Phase12.HardeningTests.Authorization;

/// <summary>
/// Commit 14's media limits. Magic-byte sniffing already proved the bytes are a real PNG/JPEG;
/// these prove the far more expensive question is asked too — what decoding them would cost.
/// </summary>
public sealed class MediaUploadLimitTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void A_normal_poster_is_accepted()
    {
        Assert.True(ImageLimits.TryValidate(EncodePng(1200, 800), out var error), error);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void A_decompression_bomb_is_rejected_from_its_header_without_being_decoded()
    {
        // A single-colour 12000x12000 PNG compresses to a trivially small file and expands to
        // roughly 576 MB of RGBA. It is a completely valid image — which is the point: nothing
        // about the bytes themselves is suspicious, only what decoding them would allocate.
        var bomb = EncodePng(12_000, 12_000);
        Assert.True(bomb.Length < ImageLimits.MaxBytes, "The bomb must pass a size check to be worth testing.");

        Assert.False(ImageLimits.TryValidate(bomb, out var error));
        Assert.Contains("edge", error);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void A_pixel_count_that_would_overflow_a_32_bit_multiply_is_still_rejected()
    {
        // 9000x9000 stays under the per-edge cap, so only the pixel budget can catch it — and that
        // multiply is done in 64-bit on purpose. In 32-bit arithmetic a large enough product wraps
        // negative and slips under the limit, which would wave through precisely the input the
        // check exists to stop.
        var wide = EncodePng(9_000, 9_000);

        Assert.False(ImageLimits.TryValidate(wide, out var error));
        Assert.Contains("pixels", error);
        Assert.True(9_000L * 9_000 > ImageLimits.MaxPixels);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void Something_that_is_not_an_image_at_all_is_rejected()
    {
        Assert.False(ImageLimits.TryValidate("not an image"u8.ToArray(), out var error));
        Assert.NotNull(error);
    }

    /// <summary>
    /// A blank bitmap encoded as PNG — uniform colour, so it compresses to almost nothing while
    /// declaring its full dimensions in the header, exactly like a real bomb.
    /// </summary>
    private static byte[] EncodePng(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        using var bitmap = new SKBitmap(info);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
