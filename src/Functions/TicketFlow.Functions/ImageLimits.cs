using SkiaSharp;

namespace TicketFlow.Functions;

/// <summary>
/// Bounds on what a poster upload is allowed to cost to decode (Phase 12 Commit 14).
/// </summary>
/// <remarks>
/// Magic-byte sniffing (see <see cref="ImageFormat"/>) proves the bytes ARE a PNG or JPEG. It says
/// nothing about what decoding them costs, and those are different questions: a ~450 KB PNG can
/// declare 50000x50000 pixels, which is a perfectly valid image that expands to roughly 10 GB of
/// RGBA once <c>SKBitmap.Decode</c> materializes it — the compressed size is not a proxy for the
/// decoded size, which is exactly what makes a decompression bomb work.
///
/// The check reads the header only, via <see cref="SKCodec"/>, so it never allocates the pixel
/// buffer it is deciding about. That ordering is the whole point: validating after a decode would
/// mean the process is already dead.
/// </remarks>
internal static class ImageLimits
{
    /// <summary>
    /// Largest accepted upload. Four MiB remains below Lambda's six-MiB synchronous Invoke
    /// transport ceiling after base64 expansion and JSON framing.
    /// </summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>Largest accepted single edge, so an extreme aspect ratio cannot slip under the pixel budget.</summary>
    public const int MaxDimension = 10_000;

    /// <summary>
    /// Decoded pixel budget: 40 megapixels is ~160 MB as RGBA, which a Lambda can hold. This is the
    /// limit that actually stops a bomb — dimensions alone would let 9999x9999 through.
    /// </summary>
    public const long MaxPixels = 40_000_000;

    public static bool TryValidate(byte[] bytes, out string? error)
    {
        if (bytes.Length > MaxBytes)
        {
            error = $"Image is {bytes.Length} bytes; the limit is {MaxBytes}.";
            return false;
        }

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            error = "Image header could not be read.";
            return false;
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            error = "Image reports a non-positive dimension.";
            return false;
        }

        if (info.Width > MaxDimension || info.Height > MaxDimension)
        {
            error = $"Image is {info.Width}x{info.Height}; each edge must be at most {MaxDimension}px.";
            return false;
        }

        // long, not int: 50000*50000 overflows a 32-bit multiply and would come back NEGATIVE,
        // sailing under the limit — the check would then wave through the exact input it exists
        // to stop.
        var pixels = (long)info.Width * info.Height;
        if (pixels > MaxPixels)
        {
            error = $"Image decodes to {pixels} pixels; the limit is {MaxPixels}.";
            return false;
        }

        error = null;
        return true;
    }
}
