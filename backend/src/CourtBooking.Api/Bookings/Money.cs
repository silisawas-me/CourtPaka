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
    public static decimal HeldFor(PaymentState payment, decimal takenBaht, decimal askedBaht) =>
        Math.Max(0m, payment == PaymentState.Received ? Math.Max(takenBaht, askedBaht) : takenBaht);

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
