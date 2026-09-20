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
    /// What the booker would get back, where it would leave the money, and what stands in the way
    /// if anything does.
    /// </summary>
    /// <param name="Refused">The reason this cannot be cancelled, or null if it can.</param>
    /// <param name="RefundPercent">The share the ending gives back, whoever ends up holding it.</param>
    /// <param name="RefundBaht">What would be owed back today, which is nothing until the money is known to have arrived.</param>
    /// <param name="Payment">Where cancelling would leave the payment state (PRD 6.2).</param>
    public readonly record struct Offer(
        string? Refused,
        int RefundPercent,
        decimal RefundBaht,
        PaymentState Payment)
    {
        public bool Allowed => Refused is null;

        /// <summary>
        /// Nobody has said yet whether the money arrived, so the amount is not settled. The
        /// booker is told that rather than being shown a nought they would read as a loss.
        /// </summary>
        public bool AwaitsVenue => Payment == PaymentState.Unconfirmed;
    }

    /// <summary>
    /// What cancelling this booking right now would come to.
    ///
    /// A hold may be let go until it lapses; a booking that has been paid for may be let go until
    /// play is due to start. After that the hours have begun and there is nothing to give back.
    ///
    /// The status is handed in rather than read off the booking, because a hold whose time is up
    /// and a confirmed booking whose hours are behind it both read as something else (PRD 9.2).
    /// </summary>
    public static Offer For(Booking booking, BookingStatus status, DateTimeOffset now)
    {
        // A booking always has hours; one read without them cannot be spoken for.
        if (booking.Slots.Count == 0)
        {
            return Refuse(BookingErrorCodes.NotCancellable, booking.PaymentState);
        }

        var playStartsAt = booking.Slots.Min(slot => slot.StartsAt);

        switch (status)
        {
            // Nothing has been paid, so nothing comes back. Letting go of a hold that has already
            // lapsed is not cancelling it — it is gone, and says so (PRD 9.2).
            case BookingStatus.Held:
                return booking.HoldExpiresAt <= now
                    ? Refuse(BookingErrorCodes.NotCancellable, booking.PaymentState)
                    : Give(booking, 0, booking.PaymentState);

            // The money may well be at the venue; only the venue can say. Until it does, the
            // booking owes nothing and sits in the venue's list of things to settle (PRD 6.2).
            case BookingStatus.PendingVerification:
                return now >= playStartsAt
                    ? Refuse(BookingErrorCodes.PlayHasStarted, booking.PaymentState)
                    : Give(booking, Refunds.AllOfIt, PaymentState.Unconfirmed);

            // The venue has the money, so the terms the booking was made under decide the rest.
            // Only this answer needs the terms, so only this answer insists on having them: a
            // booking just created carries the id of its policy and not the policy itself.
            case BookingStatus.Confirmed when booking.CancellationPolicy is { } policy:
                return now >= playStartsAt
                    ? Refuse(BookingErrorCodes.PlayHasStarted, booking.PaymentState)
                    : Give(
                        booking,
                        policy.RefundPercentFor(now, playStartsAt),
                        booking.PaymentState);

            default:
                return Refuse(BookingErrorCodes.NotCancellable, booking.PaymentState);
        }
    }

    private static Offer Give(Booking booking, int percent, PaymentState payment) =>
        new(null, percent, Refunds.DueFor(BookingStatus.Cancelled, payment, booking.TotalBaht, percent), payment);

    private static Offer Refuse(string code, PaymentState payment) => new(code, 0, 0m, payment);
}
