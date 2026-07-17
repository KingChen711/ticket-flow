namespace TicketFlow.Functions;

/// <summary>
/// Content-Type is client-supplied metadata, not proof of what the bytes actually are — a
/// magic-byte check is the only way "reject non-images" (docs/phase-9-plan.md Commit 2 DoD)
/// means anything. PNG/JPEG only: the only 2 formats a poster upload realistically needs, and the
/// only 2 SKBitmap.Decode needs to reliably handle for this Commit.
/// </summary>
internal static class ImageFormat
{
    public static string? DetectExtension(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "jpg";
        return null;
    }

    public static string ContentType(string extension) => extension switch
    {
        "png" => "image/png",
        "jpg" => "image/jpeg",
        _ => "application/octet-stream",
    };
}
