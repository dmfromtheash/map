namespace Map.Core.Documents;

public enum ProjectAssetImageFormat
{
    Png,
    Jpeg,
    Webp,
}

/// <summary>
/// The document-level image allowlist. Persistence can reject a disguised payload from its
/// bounded header without depending on a renderer; the Skia boundary still verifies the codec's
/// actual encoded format before metadata inspection or pixel decode.
/// </summary>
public static class ProjectAssetFormatPolicy
{
    private static ReadOnlySpan<byte> PngSignature =>
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public static bool TryGetDeclaredFormat(
        string? mediaType, string? extension, out ProjectAssetImageFormat format)
    {
        var normalizedExtension = extension?.TrimStart('.');
        if (string.Equals(mediaType, "image/png", StringComparison.OrdinalIgnoreCase)
            && string.Equals(normalizedExtension, "png", StringComparison.OrdinalIgnoreCase))
        {
            format = ProjectAssetImageFormat.Png;
            return true;
        }

        if (string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(normalizedExtension, "jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedExtension, "jpeg", StringComparison.OrdinalIgnoreCase)))
        {
            format = ProjectAssetImageFormat.Jpeg;
            return true;
        }

        if (string.Equals(mediaType, "image/webp", StringComparison.OrdinalIgnoreCase)
            && string.Equals(normalizedExtension, "webp", StringComparison.OrdinalIgnoreCase))
        {
            format = ProjectAssetImageFormat.Webp;
            return true;
        }

        format = default;
        return false;
    }

    public static bool HasExpectedHeader(ReadOnlySpan<byte> bytes, ProjectAssetImageFormat format) =>
        format switch
        {
            ProjectAssetImageFormat.Png => bytes.Length >= 8
                && bytes[..8].SequenceEqual(PngSignature),
            ProjectAssetImageFormat.Jpeg => bytes.Length >= 3
                && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
            ProjectAssetImageFormat.Webp => bytes.Length >= 12
                && bytes[..4].SequenceEqual("RIFF"u8)
                && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };
}
