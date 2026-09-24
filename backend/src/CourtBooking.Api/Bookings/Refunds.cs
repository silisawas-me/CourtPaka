namespace CourtBooking.Api.Bookings;

/// <summary>
/// What the venue owes back, and when (PRD 6.2). One place, because the answer depends on three
/// things that move independently — where the booking ended up, whether the money ever arrived,
/// and the share that ending gives back — and every screen that shows an amount has to agree
/// about it.
///
/// The money only comes back if it went in: a booking whose payment state is anything but
/// <see cref="PaymentState.Received"/> owes nothing, whatever happened to it. That is why the
/// share is stored on the booking and the amount is worked out from it each time: the venue can
/// say weeks later that the money did arrive (US-13), and the booker must then be owed what they
/// were shown when they cancelled, not what today's terms would give.
/// </summary>
public static class Refunds
{
    /// <summary>
    /// Where a booking can end up owing money back. Every other status owes nothing — a booking
    /// still being played is not a refund waiting to happen.
    /// </summary>
    private static readonly BookingStatus[] Endings =
    [
        BookingStatus.Rejected,
        BookingStatus.Cancelled,
        BookingStatus.NoShow,
    ];

    /// <summary>
    /// The amount owed for a booking, from where it ended, the share it ended with, and how much
    /// of it the venue is actually holding (PRD 6.2, US-28).
    ///
    /// A venue cannot give back more than it has. A booking held on a deposit has had part of its
    /// price arrive, so a policy that gives everything back gives back the deposit, not the price.
    /// </summary>
    /// <param name="heldBaht">
    /// What the venue is holding for this booking: the receipts written against it (PRD US-26).
    /// Ignored where the payment state already says the whole of it arrived — that answer is
    /// older than receipts are, and some bookings reached it without one.
    /// </param>
    public static decimal DueFor(
        BookingStatus status,
        PaymentState payment,
        decimal totalBaht,
        int refundPercent,
        decimal heldBaht) =>
        Endings.Contains(status)
            ? Math.Min(Share(totalBaht, refundPercent), Holding(payment, totalBaht, heldBaht))
            : 0m;

    /// <summary>
    /// A share of a booking in baht, to the satang. Rounded away from zero, so a half satang goes
    /// to the booker rather than the venue.
    /// </summary>
    private static decimal Share(decimal totalBaht, int percent) =>
        Math.Round(totalBaht * percent / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>What the venue has of this booking's money, which is the ceiling on what it owes.</summary>
    private static decimal Holding(PaymentState payment, decimal totalBaht, decimal heldBaht) =>
        payment == PaymentState.Received ? totalBaht : Math.Max(0m, heldBaht);

    /// <summary>The whole of it. A venue that refuses hours it took money for keeps none (PRD 6.1).</summary>
    public const int AllOfIt = 100;

    /// <summary>
    /// The records that count towards what has been sent back. A voided one stays in the table —
    /// what the venue said at the time is part of the history — and stops counting. Every screen
    /// that shows how much came back reads the rule from here (PRD 6.2, US-18).
    /// </summary>
    public static IQueryable<RefundRecord> StillStanding(this IQueryable<RefundRecord> records) =>
        records.Where(record => record.VoidedAt == null);

    /// <summary>
    /// What is still owed after what has already been sent back (PRD 6.2). This is the number a
    /// venue acts on: what it owes is a fact about the booking, and what it still has to send is
    /// what it has to do about it.
    ///
    /// It never goes below zero, because a venue cannot owe less than nothing. That also means it
    /// cannot show that more went out than was owed — the write path is what keeps that from
    /// happening (one transaction, one lock per booking), and nothing here would notice if it did.
    /// </summary>
    public static decimal OutstandingOf(decimal refundDueBaht, decimal sentBackBaht) =>
        Math.Max(0m, refundDueBaht - sentBackBaht);
}
