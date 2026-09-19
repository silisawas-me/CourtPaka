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
    DateTimeOffset? SlipUploadedAt);

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
}
