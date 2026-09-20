using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// Why a venue turned a booking away (PRD 6.1). It is not a note: each answer settles a different
/// amount, so the venue picks one rather than writing prose and hoping somebody reads it.
/// </summary>
public enum CancellationReason
{
    /// <summary>The customer asked. The terms the booking was made under decide the rest.</summary>
    CustomerRequest = 1,

    /// <summary>The venue could not honour the booking. All of it goes back.</summary>
    VenueInitiated = 2,

    /// <summary>The money never arrived, or was not what it seemed. Nothing goes back.</summary>
    PaymentNotReceived = 3,
}

/// <summary>
/// What a venue may do to a booking, and what each of those does to the money (PRD US-13, 6.1).
///
/// The booker's own way out is <see cref="Cancellation"/>; this is the counter's, which is a
/// different set of doors. A venue may reach further back — it can record that nobody turned up,
/// and it can correct what it recorded for a day after the hours were played — and it has to say
/// why, because the reason is what decides the amount.
/// </summary>
public static class VenueDecisions
{
    /// <summary>
    /// How long after the hours are over a venue may still change what it recorded about them
    /// (PRD 6.1). Long enough for the next morning; short enough that a month-old booking is
    /// settled and stays settled.
    /// </summary>
    public static readonly TimeSpan CorrectionWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// How late a customer has to be before the venue may write them off (PRD 6.1). Nobody is a
    /// no-show at one minute past.
    /// </summary>
    public static readonly TimeSpan LateEnoughForNoShow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// What cancelling this booking from the counter would come to.
    ///
    /// The answers the venue has to give differ by where the booking stands, so a missing one is
    /// refused here rather than guessed at: a hold needs nothing, a booking waiting to be checked
    /// needs to know whether the money arrived, and one already paid for needs a reason.
    /// </summary>
    public static Cancellation.Offer CancelOffer(
        Booking booking,
        BookingStatus status,
        CancellationReason? reason,
        bool? paymentReceived,
        bool byOwner,
        DateTimeOffset now)
    {
        if (booking.Slots.Count == 0)
        {
            return Refuse(BookingErrorCodes.NotCancellable, booking.PaymentState);
        }

        var playStartsAt = booking.Slots.Min(slot => slot.StartsAt);
        var playEndsAt = booking.Slots.Max(slot => slot.EndsAt);

        switch (status)
        {
            // Nothing has been paid and nothing is owed. No reason either: a hold let go at the
            // counter is the same nothing as a hold let go by the booker (PRD 6.1).
            case BookingStatus.Held:
                return booking.HoldExpiresAt <= now
                    ? Refuse(BookingErrorCodes.NotCancellable, booking.PaymentState)
                    : Give(booking, 0, booking.PaymentState);

            // The venue is the one who can see the bank account, so here it says outright whether
            // the money arrived, and that is what settles the amount (PRD 6.1, 6.2).
            case BookingStatus.PendingVerification when paymentReceived is null:
                return Refuse(BookingErrorCodes.PaymentAnswerRequired, booking.PaymentState);

            case BookingStatus.PendingVerification:
                return paymentReceived.Value
                    ? Give(booking, Refunds.AllOfIt, PaymentState.Received)
                    : Give(booking, 0, PaymentState.NotReceived);

            case BookingStatus.Confirmed when reason is null:
                return Refuse(BookingErrorCodes.ReasonRequired, booking.PaymentState);

            // Up to the moment the hours are over. A venue cancelling halfway through is giving
            // up the rest of the booking, which the terms have an answer for (PRD 6.1).
            case BookingStatus.Confirmed:
                return now >= playEndsAt
                    ? Refuse(BookingErrorCodes.PlayHasStarted, booking.PaymentState)
                    : ForReason(booking, reason.Value, playStartsAt, now);

            // What was played is written down; correcting it is the owner's, and only while the
            // day is still fresh (PRD 6.1).
            case BookingStatus.Completed when !byOwner:
                return Refuse(BookingErrorCodes.OwnerOnly, booking.PaymentState);

            case BookingStatus.Completed when reason is null:
                return Refuse(BookingErrorCodes.ReasonRequired, booking.PaymentState);

            case BookingStatus.Completed
                when reason is CancellationReason.CustomerRequest:
                // Asking for hours that have already been played back is not a request anybody
                // can grant. The other two reasons say something true about them (PRD 6.1).
                return Refuse(BookingErrorCodes.ReasonNotAllowedHere, booking.PaymentState);

            case BookingStatus.Completed:
                return WithinCorrectionWindow(playEndsAt, now)
                    ? ForReason(booking, reason.Value, playStartsAt, now)
                    : Refuse(BookingErrorCodes.TooLateToCorrect, booking.PaymentState);

            default:
                return Refuse(BookingErrorCodes.NotCancellable, booking.PaymentState);
        }
    }

    /// <summary>
    /// Whether the venue may record that nobody turned up, and what that leaves (PRD 6.1). It is
    /// always nothing: the court was held, nobody came, and the money stays where it is.
    /// </summary>
    public static Cancellation.Offer NoShowOffer(
        Booking booking,
        BookingStatus status,
        DateTimeOffset now)
    {
        if (booking.Slots.Count == 0)
        {
            return Refuse(BookingErrorCodes.NotCancellable, booking.PaymentState);
        }

        var playStartsAt = booking.Slots.Min(slot => slot.StartsAt);
        var playEndsAt = booking.Slots.Max(slot => slot.EndsAt);

        return status switch
        {
            // Nobody is a no-show at one minute past, and once the hours are over the booking has
            // already been played as far as everything else is concerned (PRD 6.1, 9.2).
            BookingStatus.Confirmed when now < playStartsAt + LateEnoughForNoShow =>
                Refuse(BookingErrorCodes.NotYetLateEnough, booking.PaymentState),

            BookingStatus.Confirmed => Give(booking, 0, booking.PaymentState),

            // The hours are behind them and the venue is correcting what it recorded.
            BookingStatus.Completed when WithinCorrectionWindow(playEndsAt, now) =>
                Give(booking, 0, booking.PaymentState),

            BookingStatus.Completed =>
                Refuse(BookingErrorCodes.TooLateToCorrect, booking.PaymentState),

            _ => Refuse(BookingErrorCodes.NotCancellable, booking.PaymentState),
        };
    }

    /// <summary>
    /// Whether the venue may take a no-show back — somebody did turn up, and it was recorded
    /// wrongly. The owner's to press, inside the correction window, and never once the hours have
    /// been sold to somebody else (PRD 6.1); that check is the caller's, since it is a question
    /// for the database.
    /// </summary>
    public static Cancellation.Offer PlayedAfterAllOffer(
        Booking booking,
        BookingStatus status,
        string? reason,
        bool byOwner,
        DateTimeOffset now)
    {
        if (status != BookingStatus.NoShow || booking.Slots.Count == 0)
        {
            return Refuse(BookingErrorCodes.NotCancellable, booking.PaymentState);
        }

        if (!byOwner)
        {
            return Refuse(BookingErrorCodes.OwnerOnly, booking.PaymentState);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Refuse(BookingErrorCodes.ReasonRequired, booking.PaymentState);
        }

        return WithinCorrectionWindow(booking.Slots.Max(slot => slot.EndsAt), now)
            ? Give(booking, 0, booking.PaymentState)
            : Refuse(BookingErrorCodes.TooLateToCorrect, booking.PaymentState);
    }

    /// <summary>
    /// The venue finally saying whether the money arrived, for a booking the booker gave up while
    /// it was still being checked (PRD US-13, 6.2). Until this is answered the booking owes
    /// nothing and nobody knows whether it should.
    /// </summary>
    public static Cancellation.Offer SettlementOffer(Booking booking, bool paymentReceived) =>
        booking.PaymentState == PaymentState.Unconfirmed
            ? new Cancellation.Offer(
                null,
                booking.RefundPercent,
                Refunds.DueFor(
                    booking.Status,
                    paymentReceived ? PaymentState.Received : PaymentState.NotReceived,
                    booking.TotalBaht,
                    booking.RefundPercent),
                paymentReceived ? PaymentState.Received : PaymentState.NotReceived)
            : Refuse(BookingErrorCodes.NothingToSettle, booking.PaymentState);

    /// <summary>What the chosen reason gives back (PRD 6.1).</summary>
    private static Cancellation.Offer ForReason(
        Booking booking,
        CancellationReason reason,
        DateTimeOffset playStartsAt,
        DateTimeOffset now) =>
        reason switch
        {
            // The venue could not honour it, so the venue keeps nothing.
            CancellationReason.VenueInitiated => Give(booking, Refunds.AllOfIt, booking.PaymentState),

            // There was never any money to give back, and the record should say so.
            CancellationReason.PaymentNotReceived => Give(booking, 0, PaymentState.NotReceived),

            // The customer asked, so the terms they booked under answer — and asking after the
            // hours have begun is asking too late (PRD BR-06).
            _ when booking.CancellationPolicy is { } policy =>
                Give(booking, policy.RefundPercentFor(now, playStartsAt), booking.PaymentState),

            _ => Refuse(BookingErrorCodes.NotCancellable, booking.PaymentState),
        };

    private static bool WithinCorrectionWindow(DateTimeOffset playEndsAt, DateTimeOffset now) =>
        now < playEndsAt + CorrectionWindow;

    private static Cancellation.Offer Give(Booking booking, int percent, PaymentState payment) =>
        new(
            null,
            percent,
            Refunds.DueFor(BookingStatus.Cancelled, payment, booking.TotalBaht, percent),
            payment);

    private static Cancellation.Offer Refuse(string code, PaymentState payment) =>
        new(code, 0, 0m, payment);
}
