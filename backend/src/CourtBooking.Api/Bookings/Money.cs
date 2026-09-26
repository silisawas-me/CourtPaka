using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
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
/// One amount the venue took (PRD US-26). Rows are only added: a deposit and the rest are two of
/// these, and nothing edits either afterwards — the day is counted from them, and a count you can
/// rewrite is not a count.
///
/// It is not the same thing as <see cref="PaymentState"/>, which says whether the venue considers
/// itself paid. These say what actually came in, when, in what form, and from whose hands.
///
/// Most of it is money for a booking. Selling a package is money too, and it goes in the same
/// till on the same day (PRD US-31), so it is one of these as well — with the package named
/// instead of a booking. Exactly one of the two, which the database sees to.
/// </summary>
public sealed class PaymentReceipt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The booking it was for, or null where it was a package being sold.</summary>
    public Guid? BookingId { get; init; }

    /// <summary>The package that was sold, or null where it was money for a booking (US-31).</summary>
    public Guid? PackageId { get; init; }

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

    public HourPackage? Package { get; init; }

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
/// The kinds of row that can be what a till is out by (PRD US-26). Not a diagnosis: each one is
/// an amount that happens to match, offered to somebody who can go and look.
/// </summary>
public enum MoneyLeadKind
{
    /// <summary>Cash written down as taken. Short by exactly this = it was never in the drawer.</summary>
    CashTaken = 1,

    /// <summary>Cash written down as handed back. Over by exactly this = it never left.</summary>
    CashHandedBack = 2,

    /// <summary>A booking of that day still owing this. Over by exactly this = taken, not written.</summary>
    StillOwed = 3,
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
    /// What the venue has of a booking's money (PRD US-26, US-28). The receipts, except where the
    /// payment state says the money arrived and no receipt says how much — bookings reached that
    /// answer before receipts existed, and what arrived was what was asked for. Every booking made
    /// before deposits were possible was asked for its whole price.
    /// </summary>
    /// <param name="packageBaht">
    /// What a package's hours paid for this booking (PRD US-31), or null where no package did.
    /// Where there is one it is the whole answer: the money came in when the package was sold, at
    /// what the customer paid for an hour then — not at the price on the board the day the hours
    /// were spent, which is what they chose not to pay. Nothing else is owed, because hours pay
    /// instead of money.
    ///
    /// Null and zero are different answers. A package booking that was cancelled and gave all of
    /// its hours back kept nothing, and reading that as "no package" would hand it the whole
    /// price as money the venue is holding.
    /// </param>
    public static decimal HeldFor(
        PaymentState payment,
        decimal takenBaht,
        decimal askedBaht,
        decimal? packageBaht = null) =>
        packageBaht is { } worth
            ? Math.Max(0m, decimal.Round(worth, 2, MidpointRounding.AwayFromZero))
            : Math.Max(
                0m,
                payment == PaymentState.Received
                    ? Math.Max(takenBaht, askedBaht)
                    : takenBaht);

    /// <summary>
    /// Whether money may still be taken for this booking (PRD US-26). Something has to be owed,
    /// and the booking has to be one the venue is going to honour or has already played: a
    /// booking that was turned away or let go owes nothing forwards, and money against it would
    /// be a refund question wearing the wrong hat.
    ///
    /// A venue that already says it has the money is asked for none, whatever the receipts add up
    /// to. Payment state is the older answer and some bookings reached it without one — a slip
    /// accepted before receipts were written down, a venue answering for the money after the fact
    /// (PRD US-13) — and offering a door there is offering to collect twice.
    ///
    /// A hold is not one of them. PRD 6.1 leaves a hold two ways out — a slip, or the clock — and
    /// a booker who paid at the desk instead is sold the hours at the counter (US-13), which is
    /// the answer the state machine already has.
    ///
    /// The one place that answers it, so the door the counter sees and the door the server opens
    /// are the same door.
    /// </summary>
    public static bool CanTake(BookingStatus status, PaymentState payment, decimal outstanding) =>
        outstanding > 0 && payment != PaymentState.Received && StillOwing.Contains(status);

    /// <summary>
    /// The bookings a venue may still be owed for. Written once, because the door on one row and
    /// the day's outstanding total are the same question asked twice.
    /// </summary>
    public static readonly BookingStatus[] StillOwing =
    [
        BookingStatus.PendingVerification,
        BookingStatus.Confirmed,
        BookingStatus.Completed,
        BookingStatus.NoShow,
    ];

    /// <summary>
    /// Whether paying the last of it settles the booking itself and not only its money. A booking
    /// waiting on a slip that was paid another way would otherwise wait for a slip that is never
    /// coming — the money arriving is the same answer the slip queue gives (PRD US-12, 6.1).
    /// </summary>
    public static bool PayingInFullConfirms(BookingStatus status) =>
        BookingTransitions.CanMove(status, BookingStatus.Confirmed);

    /// <summary>
    /// When a day's takings start and stop being that day's (PRD US-26).
    ///
    /// A day is midnight to midnight until somebody counts the till. Once they have, that count
    /// is the end of it: money taken afterwards belongs to the next day, because the drawer it
    /// went into has already been counted and written down, and a count that can still move is
    /// not a count. The day after it picks that money up, which is why a day also starts at the
    /// previous day's count rather than at its midnight.
    ///
    /// It cannot chain. A day may not be closed before it happens, so the closing that ends a day
    /// is always later than anything the day before it could carry in.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset Until) TillDay(
        DateOnly day,
        DateTimeOffset? closedYesterday,
        DateTimeOffset? closedToday)
    {
        var midnight = PlatformRequirements.BangkokHour(day, 0);
        var nextMidnight = PlatformRequirements.BangkokHour(day.AddDays(1), 0);

        // Never past its own midnight, either end. A count can come days late — a venue catching
        // up on a week it never closed — and a window that ran to the moment of counting would
        // swallow every day in between.
        return (
            Earlier(closedYesterday ?? midnight, midnight),
            Earlier(closedToday ?? nextMidnight, nextMidnight));
    }

    private static DateTimeOffset Earlier(DateTimeOffset one, DateTimeOffset other) =>
        one < other ? one : other;

    /// <summary>
    /// What the till should hold at the end of the day: what it started with, plus the cash that
    /// came in, less the cash that went back out (PRD US-26). Anything that never touched the
    /// till — a transfer, a card — is not in it.
    /// </summary>
    public static decimal ExpectedCash(decimal openingFloat, decimal cashIn, decimal cashOut) =>
        decimal.Round(openingFloat + cashIn - cashOut, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Whether one amount is exactly what a count came out by (PRD US-26). Exactly, in either
    /// direction, and nothing looser: a list of rows that are roughly the right size is a list
    /// nobody reads twice. A till that balanced has nothing to explain.
    /// </summary>
    public static bool Explains(decimal amount, decimal differenceBaht) =>
        differenceBaht != 0
        && decimal.Round(Math.Abs(amount), 2, MidpointRounding.AwayFromZero)
            == decimal.Round(Math.Abs(differenceBaht), 2, MidpointRounding.AwayFromZero);
}
