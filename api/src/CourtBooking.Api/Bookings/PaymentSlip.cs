using CourtBooking.Api.Identity;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// A picture of a transfer, uploaded by the booker and checked by the venue (PRD US-04, US-12).
///
/// Slips are added, never replaced: a booker who uploads a corrected one leaves the previous row
/// in place, because what was shown to the venue at each point is part of the booking's record.
/// The newest one is the one being checked.
/// </summary>
public sealed class PaymentSlip
{
    /// <summary>Five megabytes, which is a generous phone photograph (PRD US-04).</summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid BookingId { get; init; }

    public required Guid UploadedByUserId { get; init; }

    public required DateTimeOffset UploadedAt { get; init; }

    /// <summary>Where the bytes are, as the store names them. Never a name the booker chose.</summary>
    public required string StoredName { get; init; }

    public required string ContentType { get; init; }

    public required long ByteSize { get; init; }

    /// <summary>
    /// SHA-256 of the bytes, lower-case hex. It is what catches the same file being sent for two
    /// bookings at one venue; a re-photographed or cropped slip gets past it (PRD BR-07, R4).
    /// </summary>
    public required string Sha256 { get; init; }

    /// <summary>
    /// The earlier slip at this venue carrying the same bytes, if there is one. The booker is not
    /// told; it is a flag on the venue's checking queue (PRD BR-07).
    /// </summary>
    public Guid? SameBytesAsSlipId { get; init; }

    public Booking? Booking { get; init; }

    public AppUser? UploadedBy { get; init; }
}
