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

    /// <summary>The amount owed for a booking, from where it ended and the share it ended with.</summary>
    public static decimal DueFor(
        BookingStatus status,
        PaymentState payment,
        decimal totalBaht,
        int refundPercent) =>
        Endings.Contains(status) ? Share(totalBaht, refundPercent, payment) : 0m;

    /// <summary>
    /// A share of a booking in baht, to the satang — nothing at all unless the money arrived.
    /// Rounded away from zero, so a half satang goes to the booker rather than the venue.
    /// </summary>
    private static decimal Share(decimal totalBaht, int percent, PaymentState payment) =>
        payment == PaymentState.Received
            ? Math.Round(totalBaht * percent / 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;

    /// <summary>The whole of it. A venue that refuses hours it took money for keeps none (PRD 6.1).</summary>
    public const int AllOfIt = 100;

    /// <summary>
    /// What is still owed after what has already been sent back (PRD 6.2). This is the number a
    /// venue acts on: what it owes is a fact about the booking, and what it still has to send is
    /// what it has to do about it.
    /// </summary>
    public static decimal OutstandingOf(decimal refundDueBaht, decimal sentBackBaht) =>
        Math.Max(0m, refundDueBaht - sentBackBaht);
}
