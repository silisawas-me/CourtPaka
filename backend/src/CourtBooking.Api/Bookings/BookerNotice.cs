namespace CourtBooking.Api.Bookings;

/// <summary>What a booker is told about (PRD US-06). Stored, so the numbers must not move.</summary>
public enum BookerNoticeKind
{
    /// <summary>A hold was made and is waiting to be paid for.</summary>
    Held = 1,
    Confirmed = 2,

    /// <summary>The venue turned the slip down, and says why.</summary>
    Rejected = 3,

    /// <summary>The hold ran out before a slip arrived.</summary>
    Expired = 4,

    /// <summary>Cancelled by either side, with the reason and what is owed back.</summary>
    Cancelled = 5,

    /// <summary>The venue recorded sending money back (PRD US-18).</summary>
    RefundRecorded = 6,

    /// <summary>Two hours before play, for a confirmed booking only (PRD US-06, S-05).</summary>
    AboutToPlay = 7,

    /// <summary>
    /// Hours came free on a day they were waiting for, and are being held for them (PRD US-27).
    /// The hold behind it is an ordinary one; what is different is that they did not ask for it
    /// just now, so the letter has to say where it came from.
    /// </summary>
    WaitlistOffer = 9,

    /// <summary>
    /// The venue said, after a cancellation, whether the money arrived — which is what decides
    /// what comes back (PRD 6.2, US-13). The cancellation message promised this one.
    /// </summary>
    PaymentSettled = 8,
}

/// <summary>
/// The receipt for one message to a booker: which thing it was about, and when it was claimed
/// (PRD US-06).
///
/// Written before the message goes out, not after, and unique on what it is about — so two
/// instances, or two ticks of one, cannot both send it. The other order sends twice whenever the
/// write after the send fails, and a booker told twice that their booking was cancelled starts
/// wondering whether it was cancelled twice.
///
/// <see cref="SourceId"/> is whatever the message is about: the status change for a move, the
/// refund record for a refund, and the booking itself for the reminder before play.
/// </summary>
public sealed class BookerNotice
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid BookingId { get; init; }

    public required BookerNoticeKind Kind { get; init; }

    public required Guid SourceId { get; init; }

    public required DateTimeOffset ClaimedAt { get; init; }

    public Booking? Booking { get; init; }
}
