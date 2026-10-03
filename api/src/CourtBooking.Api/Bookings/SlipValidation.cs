namespace CourtBooking.Api.Bookings;

/// <summary>
/// What a slip has to be before the server keeps it. The declared content type is what the
/// uploader says it is, so the first bytes are read instead: a file named .jpg carrying something
/// else is refused rather than stored and served back later.
///
/// One table carries all three facts about a shape — how to recognise it, what to call it, and
/// what to name the file — so adding a format is one line rather than three files.
/// </summary>
public static class SlipValidation
{
    private static readonly (byte[] Magic, string ContentType, string Extension)[] Shapes =
    [
        ([0xFF, 0xD8, 0xFF], "image/jpeg", ".jpg"),
        ([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], "image/png", ".png"),
        ([0x25, 0x50, 0x44, 0x46], "application/pdf", ".pdf"),
    ];

    /// <summary>Enough to recognise every shape we accept.</summary>
    public static readonly int SniffBytes = Shapes.Max(shape => shape.Magic.Length);

    /// <summary>
    /// The content type these bytes actually are, or null for anything we do not take. The answer
    /// is what gets stored and served, not the header the uploader sent.
    /// </summary>
    public static string? ContentTypeOf(ReadOnlySpan<byte> start)
    {
        foreach (var (magic, contentType, _) in Shapes)
        {
            if (start.StartsWith(magic))
            {
                return contentType;
            }
        }

        return null;
    }

    /// <summary>The file extension for a content type this module recognised.</summary>
    public static string ExtensionOf(string contentType) =>
        Shapes.Single(shape => shape.ContentType == contentType).Extension;

    /// <summary>
    /// The content type of a file the store named, read back from the extension it was given.
    /// The store names its own files from a type this module recognised, so the extension is
    /// this module's own word and not anything an uploader chose.
    /// </summary>
    public static string ContentTypeOfName(string storedName) =>
        Shapes
            .Where(shape => storedName.EndsWith(shape.Extension, StringComparison.OrdinalIgnoreCase))
            .Select(shape => shape.ContentType)
            .FirstOrDefault() ?? "application/octet-stream";
}
