namespace CourtBooking.Api.Bookings;

/// <summary>
/// What a slip has to be before the server keeps it. The declared content type is what the
/// uploader says it is, so the first bytes are read instead: a file named .jpg carrying something
/// else is refused rather than stored and served back later.
/// </summary>
public static class SlipValidation
{
    /// <summary>Enough to recognise every shape we accept.</summary>
    public const int SniffBytes = 8;

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46];

    /// <summary>
    /// The content type these bytes actually are, or null for anything we do not take. The answer
    /// is what gets stored and served, not the header the uploader sent.
    /// </summary>
    public static string? ContentTypeOf(ReadOnlySpan<byte> start) =>
        start.StartsWith(Jpeg) ? "image/jpeg"
        : start.StartsWith(Png) ? "image/png"
        : start.StartsWith(Pdf) ? "application/pdf"
        : null;
}
