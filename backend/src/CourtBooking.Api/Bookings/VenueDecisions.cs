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
    /// How late a customer has to be before the venue may write them off (PRD 6.1, US-24). Nobody
    /// is a no-show at one minute past. A venue may set its own (Venue.GraceMinutes); this is what
    /// it starts at, and what everything that has no venue to hand uses.
    /// </summary>
    public const int DefaultGraceMinutes = 15;

    public static readonly TimeSpan LateEnoughForNoShow = TimeSpan.FromMinutes(DefaultGraceMinutes);

    /// <summary>The longest wait a venue may set, so a booking cannot be un-missable.</summary>
    public const int MaxGraceMinutes = 60;

    /// <summary>When this booking's grace runs out, after which it may be written off.</summary>
    public static DateTimeOffset GraceEndsAt(DateTimeOffset playStartsAt, int graceMinutes) =>
        playStartsAt + TimeSpan.FromMinutes(graceMinutes);

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
        if (Played(booking) is not { } played)
        {
            return Shut(booking, BookingErrorCodes.NotCancellable);
        }

        var (playStartsAt, playEndsAt) = played;

        switch (status)
        {
            // Letting a hold go at the counter is the same nothing as the booker letting it go,
            // and needs no more said about it (PRD 6.1).
            case BookingStatus.Held:
                return Cancellation.For(booking, status, now);

            // The venue is the one who can see the bank account, so here it says outright whether
            // the money arrived, and that is what settles the amount (PRD 6.1, 6.2).
            case BookingStatus.PendingVerification when paymentReceived is null:
                return Shut(booking, BookingErrorCodes.PaymentAnswerRequired);

            case BookingStatus.PendingVerification:
                return paymentReceived.Value
                    ? Give(booking, BookingStatus.Cancelled, Refunds.AllOfIt, PaymentState.Received)
                    : Give(booking, BookingStatus.Cancelled, 0, PaymentState.NotReceived);

            case BookingStatus.Confirmed when reason is null:
                return Shut(booking, BookingErrorCodes.ReasonRequired);

            // Up to the moment the hours are over, which is enforced by the reading rather than
            // here: a confirmed booking past its last hour is Completed to everyone (PRD 9.2) and
            // arrives in the branches below. A venue cancelling halfway through is giving up the
            // rest of the booking, which the terms have an answer for (PRD 6.1).
            case BookingStatus.Confirmed:
                return ForReason(booking, reason.Value, playStartsAt, now);

            // What was played is written down; correcting it is the owner's, and only while the
            // day is still fresh (PRD 6.1).
            case BookingStatus.Completed when !byOwner:
                return Shut(booking, BookingErrorCodes.OwnerOnly);

            case BookingStatus.Completed when reason is null:
                return Shut(booking, BookingErrorCodes.ReasonRequired);

            // Asking for hours that have already been played back is not a request anybody can
            // grant. The other two reasons say something true about them (PRD 6.1).
            case BookingStatus.Completed when reason is CancellationReason.CustomerRequest:
                return Shut(booking, BookingErrorCodes.ReasonNotAllowedHere);

            case BookingStatus.Completed:
                return StillCorrectable(playEndsAt, now)
                    ? ForReason(booking, reason.Value, playStartsAt, now)
                    : Shut(booking, BookingErrorCodes.TooLateToCorrect);

            default:
                return Shut(booking, BookingErrorCodes.NotCancellable);
        }
    }

    /// <summary>
    /// Whether the venue may record that nobody turned up, and what that leaves (PRD 6.1). It is
    /// always nothing: the court was held, nobody came, and the money stays where it is.
    ///
    /// How long it waits first is the venue's (PRD US-24); callers that have the venue to hand
    /// pass its own, and the rest get the default.
    /// </summary>
    public static Cancellation.Offer NoShowOffer(
        Booking booking,
        BookingStatus status,
        DateTimeOffset now,
        int graceMinutes = DefaultGraceMinutes)
    {
        if (Played(booking) is not { } played)
        {
            return Shut(booking, BookingErrorCodes.NotCancellable);
        }

        var (playStartsAt, playEndsAt) = played;

        return status switch
        {
            // Nobody is a no-show at one minute past, and once the hours are over the booking has
            // already been played as far as everything else is concerned (PRD 6.1, 9.2).
            BookingStatus.Confirmed when now < GraceEndsAt(playStartsAt, graceMinutes) =>
                Shut(booking, BookingErrorCodes.NotYetLateEnough),

            BookingStatus.Confirmed => Give(booking, BookingStatus.NoShow, 0, booking.PaymentState),

            // The hours are behind them and the venue is correcting what it recorded.
            BookingStatus.Completed when StillCorrectable(playEndsAt, now) =>
                Give(booking, BookingStatus.NoShow, 0, booking.PaymentState),

            BookingStatus.Completed => Shut(booking, BookingErrorCodes.TooLateToCorrect),

            _ => Shut(booking, BookingErrorCodes.NotCancellable),
        };
    }

    /// <summary>
    /// Whether the venue may take a no-show back — somebody did turn up, and it was recorded
    /// wrongly. The owner's to press, inside the correction window, and never once the hours have
    /// been sold to somebody else (PRD 6.1); that last check is the caller's, since it is a
    /// question only the database can answer.
    /// </summary>
    public static Cancellation.Offer PlayedAfterAllOffer(
        Booking booking,
        BookingStatus status,
        string? reason,
        bool byOwner,
        DateTimeOffset now)
    {
        if (status != BookingStatus.NoShow || Played(booking) is not { } played)
        {
            return Shut(booking, BookingErrorCodes.NotCancellable);
        }

        if (!byOwner)
        {
            return Shut(booking, BookingErrorCodes.OwnerOnly);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Shut(booking, BookingErrorCodes.ReasonRequired);
        }

        return StillCorrectable(played.Ends, now)
            ? Give(booking, BookingStatus.Completed, 0, booking.PaymentState)
            : Shut(booking, BookingErrorCodes.TooLateToCorrect);
    }

    /// <summary>What the chosen reason gives back (PRD 6.1).</summary>
    private static Cancellation.Offer ForReason(
        Booking booking,
        CancellationReason reason,
        DateTimeOffset playStartsAt,
        DateTimeOffset now) =>
        reason switch
        {
            // The venue could not honour it, so the venue keeps nothing.
            CancellationReason.VenueInitiated =>
                Give(booking, BookingStatus.Cancelled, Refunds.AllOfIt, booking.PaymentState),

            // There was never any money to give back, and the record should say so.
            CancellationReason.PaymentNotReceived =>
                Give(booking, BookingStatus.Cancelled, 0, PaymentState.NotReceived),

            // The customer asked, so the terms they booked under answer — and asking after the
            // hours have begun is asking too late (PRD BR-06).
            CancellationReason.CustomerRequest when booking.CancellationPolicy is { } policy => Give(
                booking,
                BookingStatus.Cancelled,
                policy.RefundPercentFor(now, playStartsAt),
                booking.PaymentState),

            // A reason nobody named is not one of the three, and nothing is decided on it.
            _ => Shut(booking, BookingErrorCodes.ReasonNotAllowedHere),
        };

    /// <summary>
    /// When the booking is played, or nothing at all for one read without its hours. Every door
    /// is measured against these two instants, so they are worked out once.
    /// </summary>
    private static (DateTimeOffset Starts, DateTimeOffset Ends)? Played(Booking booking) =>
        booking.Slots.Count == 0
            ? null
            : (booking.Slots.Min(slot => slot.StartsAt), booking.Slots.Max(slot => slot.EndsAt));

    /// <summary>
    /// The window PRD 6.1 gives a venue to change what it recorded about hours that were played:
    /// from the moment they end until a day after, the end excluded. It has a floor as well as a
    /// ceiling — what is still being played has not been recorded yet, so there is nothing to
    /// correct.
    /// </summary>
    private static bool StillCorrectable(DateTimeOffset playEndsAt, DateTimeOffset now) =>
        now >= playEndsAt && now < playEndsAt + CorrectionWindow;

    private static Cancellation.Offer Give(
        Booking booking,
        BookingStatus landing,
        int percent,
        PaymentState payment) =>
        Cancellation.Offer.Giving(booking, landing, percent, payment);

    private static Cancellation.Offer Shut(Booking booking, string code) =>
        Cancellation.Offer.Refusing(code, booking.PaymentState);
}
