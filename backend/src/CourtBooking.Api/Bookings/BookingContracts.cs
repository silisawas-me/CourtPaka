namespace CourtBooking.Api.Bookings;

/// <summary>One court-hour a booker picked, named the way the grid names it.</summary>
public sealed record BookingSlotRequest(Guid CourtId, DateOnly Date, int Hour);

public sealed record CreateBookingRequest(Guid VenueId, BookingSlotRequest[] Slots);

public sealed record BookingSlotResponse(
    Guid CourtId,
    string CourtName,
    DateOnly Date,
    int Hour,
    decimal BahtPerHour);

/// <summary>
/// A booking as its booker sees it. The total and the slots are the snapshot (PRD BR-05), and
/// <see cref="HoldExpiresAt"/> is when the hold lapses if it has not been paid for (PRD BR-02).
/// </summary>
public sealed record BookingResponse(
    Guid Id,
    Guid VenueId,
    string VenueName,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset HoldExpiresAt,
    decimal TotalBaht,
    BookingSlotResponse[] Slots,
    /// <summary>When the booker last sent a slip, if they have (PRD US-04).</summary>
    DateTimeOffset? SlipUploadedAt,
    /// <summary>Whether the venue has the money, and what it owes back (PRD 6.2).</summary>
    string PaymentState,
    decimal RefundDueBaht,
    /// <summary>
    /// Of what is owed, how much has actually been sent back. Refunds are made outside the system
    /// and written down against the booking (PRD BR-06), which is US-18 — until then nothing has
    /// been recorded and this is nothing.
    /// </summary>
    decimal RefundedBaht,
    /// <summary>What letting this booking go would mean right now (PRD US-05).</summary>
    CancellationOfferResponse Cancellation);

/// <summary>
/// What the booker would get back for cancelling, worked out by the server so that the number
/// they are shown and the number they are given are the same one (PRD US-05, BR-06).
/// </summary>
public sealed record CancellationOfferResponse(
    bool Allowed,
    /// <summary>The share of the booking this would give back, which a tiered policy makes worth saying.</summary>
    int RefundPercent,
    decimal RefundBaht,
    /// <summary>The venue has yet to say whether the money arrived, so the amount is not settled.</summary>
    bool AwaitsVenue);

/// <summary>
/// A booker's own bookings, split where US-05 asks for the split: what is still ahead of them,
/// and what is behind. The server decides which is which, because it holds the clock that every
/// other answer about these bookings is given against (PRD 9.2).
/// </summary>
public sealed record BookingHistoryResponse(
    BookingResponse[] Upcoming,
    BookingResponse[] Past);

public static class BookingErrorCodes
{
    public const string NoSlots = "booking.no_slots";
    public const string TooManySlots = "booking.too_many_slots";
    public const string DuplicateSlot = "booking.duplicate_slot";
    public const string MoreThanOneDay = "booking.more_than_one_day";
    public const string StartsTooSoon = "booking.starts_too_soon";
    public const string HourNotAvailable = "booking.hour_not_available";

    /// <summary>Someone else took one of the hours between reading the grid and confirming.</summary>
    public const string SlotJustTaken = "booking.slot_just_taken";

    /// <summary>One held booking at a time, per booker (PRD S-22).</summary>
    public const string AlreadyHolding = "booking.already_holding";

    public const string NotFound = "booking.not_found";

    /// <summary>This booking is not in a state a booker may let go of (PRD 6.1).</summary>
    public const string NotCancellable = "booking.not_cancellable";

    /// <summary>The hours have begun. There is nothing left to give up (PRD 6.1).</summary>
    public const string PlayHasStarted = "booking.play_has_started";

    /// <summary>
    /// It moved between being read and being written — usually the venue deciding about the slip.
    /// What may be done with it now depends on where it has got to, so the answer is to look again.
    /// </summary>
    public const string ChangedMeanwhile = "booking.changed_meanwhile";
}

public static class SlipErrorCodes
{
    public const string NoFile = "slip.no_file";
    public const string TooLarge = "slip.too_large";
    public const string UnsupportedFile = "slip.unsupported_file";

    /// <summary>
    /// The hold ran out before the slip arrived. The booker may well have transferred the money,
    /// so what they are told has to point them at the venue (PRD US-04, 6.1).
    /// </summary>
    public const string HoldExpired = "slip.hold_expired";

    public const string NotAwaitingPayment = "slip.not_awaiting_payment";
    public const string NoSlip = "slip.none_uploaded";

    /// <summary>The venue looked at something that is no longer waiting to be looked at.</summary>
    public const string NotAwaitingVerification = "slip.not_awaiting_verification";

    public const string ReasonRequired = "slip.reason_required";
    public const string ReasonTooLong = "slip.reason_too_long";
}

/// <summary>
/// Turning a booking away. Both answers are required: why, and whether the money arrived — the
/// second decides what goes back (PRD 6.1, 6.2).
/// </summary>
public sealed record RejectSlipRequest(string Reason, bool PaymentReceived);

/// <summary>
/// One booking waiting for the venue to look at its slip (PRD US-12). It names the booker by the
/// address they signed up with; nothing else about them is the venue's business here.
/// </summary>
public sealed record SlipQueueItemResponse(
    Guid BookingId,
    string BookerEmail,
    decimal TotalBaht,
    DateTimeOffset SlipUploadedAt,
    DateTimeOffset StartsAt,
    bool PlaysSoon,
    bool SameSlipSeenBefore);
