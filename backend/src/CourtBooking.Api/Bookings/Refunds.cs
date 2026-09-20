namespace CourtBooking.Api.Bookings;

/// <summary>
/// What the venue owes back, and when (PRD 6.2). One place, because the answer depends on two
/// things that move independently — where the booking ended up, and whether the money ever
/// arrived — and every screen that shows an amount has to agree about it.
///
/// The money only comes back if it went in: a booking whose payment state is anything but
/// <see cref="PaymentState.Received"/> owes nothing, whatever happened to it.
/// </summary>
public static class Refunds
{
    /// <summary>
    /// The amount owed for a booking that has arrived somewhere final. Statuses that are not an
    /// ending owe nothing — a booking still being played is not a refund waiting to happen.
    /// </summary>
    public static decimal DueFor(BookingStatus status, PaymentState payment, decimal totalBaht)
    {
        if (payment != PaymentState.Received)
        {
            return 0m;
        }

        return status switch
        {
            // The venue took money for hours it then refused. All of it goes back (PRD 6.1).
            BookingStatus.Rejected => totalBaht,

            // Cancelling and not turning up arrive with US-13 and US-18; until the rules for
            // those are written they owe nothing rather than a number nobody decided.
            _ => 0m,
        };
    }
}
