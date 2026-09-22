using CourtBooking.Api.Identity;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Bookings;

/// <summary>How the money reached the venue (PRD US-26). Stored, so the numbers must not move.</summary>
public enum PaymentMethod
{
    Cash = 1,
    PromptPay = 2,
    Card = 3,
}

/// <summary>
/// One amount the venue took for one booking (PRD US-26). Rows are only added: a deposit and the
/// rest are two of these, and nothing edits either afterwards — the day is counted from them, and
/// a count you can rewrite is not a count.
///
/// It is not the same thing as <see cref="PaymentState"/>, which says whether the venue considers
/// itself paid. These say what actually came in, when, in what form, and from whose hands.
/// </summary>
public sealed class PaymentReceipt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid BookingId { get; init; }

    /// <summary>Kept beside the booking's own so a venue's day can be counted in one query.</summary>
    public required Guid VenueId { get; init; }

    public required decimal AmountBaht { get; init; }

    public required PaymentMethod Method { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Whose hands. Null for the money a booker sent before anybody was at the desk.</summary>
    public Guid? ReceivedByUserId { get; init; }

    public string? Note { get; init; }

    public const int NoteMaxLength = 400;

    public Booking? Booking { get; init; }

    public Venue? Venue { get; init; }

    public AppUser? ReceivedBy { get; init; }
}

/// <summary>
/// The count at the end of a day (PRD US-26): what the till should hold, what it does hold, and
/// the difference between the two with whatever the person closing wrote about it.
///
/// One per venue per day, and it cannot be written twice or rewritten — a day that was counted
/// stays counted, and a correction is a note against the next one, not an edit to this.
/// </summary>
public sealed class DailyClosing
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    /// <summary>The venue's own day (PRD BR-10), not the reader's.</summary>
    public required DateOnly Date { get; init; }

    /// <summary>What was in the till before the day started.</summary>
    public required decimal OpeningFloatBaht { get; init; }

    /// <summary>What the day's cash says should be there: the float, plus cash in, less cash out.</summary>
    public required decimal ExpectedCashBaht { get; init; }

    /// <summary>What was actually counted.</summary>
    public required decimal CountedCashBaht { get; init; }

    /// <summary>Counted less expected: short is negative, over is positive.</summary>
    public required decimal DifferenceBaht { get; init; }

    public string? Note { get; init; }

    public required Guid ClosedByUserId { get; init; }

    public required DateTimeOffset ClosedAt { get; init; }

    public const int NoteMaxLength = 400;

    public Venue? Venue { get; init; }

    public AppUser? ClosedBy { get; init; }
}

/// <summary>
/// What a venue is owed and what it has taken (PRD US-26, 6.2). One place, because the number on
/// the screen before the money is taken and the number the endpoint refuses on have to be the same.
/// </summary>
public static class Takings
{
    /// <summary>What is still to be paid on this booking: nothing below zero, rounded to satang.</summary>
    public static decimal OutstandingOf(decimal totalBaht, decimal takenBaht) =>
        Math.Max(0m, decimal.Round(totalBaht - takenBaht, 2, MidpointRounding.AwayFromZero));

    /// <summary>
    /// What the till should hold at the end of the day: what it started with, plus the cash that
    /// came in, less the cash that went back out (PRD US-26). Anything that never touched the
    /// till — a transfer, a card — is not in it.
    /// </summary>
    public static decimal ExpectedCash(decimal openingFloat, decimal cashIn, decimal cashOut) =>
        decimal.Round(openingFloat + cashIn - cashOut, 2, MidpointRounding.AwayFromZero);
}
