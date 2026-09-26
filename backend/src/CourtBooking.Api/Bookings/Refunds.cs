namespace CourtBooking.Api.Bookings;

/// <summary>
/// What the venue owes back, and when (PRD 6.2). One place, because the answer depends on three
/// things that move independently — where the booking ended up, whether the money ever arrived,
/// and the share that ending gives back — and every screen that shows an amount has to agree
/// about it.
///
/// The money only comes back if it went in, and never more of it than went in: a venue holding a
/// deposit owes at most the deposit (US-28). That is why the share is stored on the booking and
/// the amount is worked out from it each time: the venue can say weeks later that the money did
/// arrive (US-13), and the booker must then be owed what they were shown when they cancelled,
/// not what today's terms would give.
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
    /// The amount owed for a booking: what the venue is holding, less what its terms let it keep
    /// (PRD 6.2, US-28).
    ///
    /// The share is of the price, not of the payment. A booking cancelled under terms that give
    /// half of it back leaves the venue entitled to the other half of the price — so a booker who
    /// has paid a quarter of it as a deposit gets nothing back, and one who paid all of it gets
    /// half. Taking a share of what happens to have arrived instead would hand the venue's whole
    /// cancellation fee to anybody paying a deposit.
    /// </summary>
    /// <param name="takenBaht">
    /// The receipts written against this booking (PRD US-26): what the venue actually holds.
    /// </param>
    /// <param name="askedBaht">
    /// What had to arrive to hold the hours. Read only where the payment state says the money
    /// arrived and no receipt says how much — bookings reached that answer before receipts
    /// existed, and for every one of them it is the whole price (the migration says so).
    /// </param>
    /// <param name="packageBaht">
    /// What a package's hours paid for this booking (PRD US-31), or null where no package did.
    /// Where there is one, nothing is owed back in money: what came in was hours, and hours are
    /// what go back (<see cref="Packages.HoursBack"/>). Handing money back for them would be
    /// paying twice for something the customer has already been given the use of.
    /// </param>
    public static decimal DueFor(
        BookingStatus status,
        PaymentState payment,
        decimal totalBaht,
        int refundPercent,
        decimal takenBaht,
        decimal askedBaht,
        decimal? packageBaht = null) =>
        Endings.Contains(status) && packageBaht is null
            ? Owed(Takings.HeldFor(payment, takenBaht, askedBaht), Kept(totalBaht, refundPercent))
            : 0m;

    /// <summary>
    /// What its terms let the venue keep, in baht: the part of the price the share does not give
    /// back. Rounded away from zero on the share, so a half satang goes to the booker.
    /// </summary>
    private static decimal Kept(decimal totalBaht, int percent) =>
        totalBaht - Math.Round(totalBaht * percent / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>What is left of what the venue holds once it has kept what it may. Never below nothing.</summary>
    private static decimal Owed(decimal holding, decimal kept) => Math.Max(0m, holding - kept);

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
