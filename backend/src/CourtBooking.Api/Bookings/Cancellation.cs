using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// What cancelling a booking would mean, and whether it may be done at all (PRD US-05, 6.1).
///
/// The booker is shown this before they press anything and the write is decided by it afterwards,
/// so it is one answer rather than two that could differ. It says nothing about the venue
/// cancelling on the booker's behalf; that is US-13, with its own reasons and its own amounts.
/// </summary>
public static class Cancellation
{
    /// <summary>
    /// What the booker would get back, and what stands in the way if anything does.
    /// </summary>
    /// <param name="Refused">The reason this cannot be cancelled, or null if it can.</param>
    /// <param name="RefundPercent">The share the ending gives back, whoever ends up holding it.</param>
    /// <param name="RefundBaht">What would be owed back today, which is nothing until the money is known to have arrived.</param>
    /// <param name="AwaitsVenue">The venue has not said yet whether the money arrived, so the amount is not settled (PRD 6.2).</param>
    public readonly record struct Offer(
        string? Refused,
        int RefundPercent,
        decimal RefundBaht,
        bool AwaitsVenue)
    {
        public bool Allowed => Refused is null;
    }

    /// <summary>
    /// What cancelling this booking right now would come to.
    ///
    /// A hold may be let go until it lapses; a booking that has been paid for may be let go until
    /// play is due to start. After that the hours have begun and there is nothing to give back.
    ///
    /// The status is handed in rather than read off the booking, because a hold whose time is up
    /// reads as expired everywhere else too (PRD 9.2). The policy is only needed once the venue
    /// has the money, so a caller that has not loaded it can still be told about a hold.
    /// </summary>
    public static Offer For(
        Booking booking,
        BookingStatus status,
        CancellationPolicy? policy,
        DateTimeOffset playStartsAt,
        DateTimeOffset now)
    {
        switch (status)
        {
            // Nothing has been paid, so nothing comes back. Letting go of a hold that has already
            // lapsed is not cancelling it — it is gone, and says so (PRD 9.2).
            case BookingStatus.Held:
                return booking.HoldExpiresAt <= now
                    ? Refuse(BookingErrorCodes.NotCancellable)
                    : new Offer(null, 0, 0m, false);

            // The money may well be at the venue; only the venue can say. Until it does, the
            // booking owes nothing and sits in the venue's list of things to settle (PRD 6.2).
            case BookingStatus.PendingVerification:
                return now >= playStartsAt
                    ? Refuse(BookingErrorCodes.PlayHasStarted)
                    : new Offer(null, 100, 0m, true);

            // The venue has the money, so the terms the booking was made under decide the rest.
            case BookingStatus.Confirmed:
                if (now >= playStartsAt)
                {
                    return Refuse(BookingErrorCodes.PlayHasStarted);
                }

                if (policy is null)
                {
                    return Refuse(BookingErrorCodes.NotCancellable);
                }

                var percent = policy.RefundPercentFor(now, playStartsAt);
                return new Offer(
                    null,
                    percent,
                    Refunds.Share(booking.TotalBaht, percent, booking.PaymentState),
                    false);

            default:
                return Refuse(BookingErrorCodes.NotCancellable);
        }
    }

    /// <summary>Where a cancelled booking leaves the money (PRD 6.2).</summary>
    public static PaymentState PaymentAfter(BookingStatus from, PaymentState payment) =>
        from == BookingStatus.PendingVerification ? PaymentState.Unconfirmed : payment;

    private static Offer Refuse(string code) => new(code, 0, 0m, false);
}
