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
    BookingSlotResponse[] Slots);

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
}
