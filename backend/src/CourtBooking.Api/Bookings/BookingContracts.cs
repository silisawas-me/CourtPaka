namespace CourtBooking.Api.Bookings;

/// <summary>One court-hour a booker picked, named the way the grid names it.</summary>
public sealed record BookingSlotRequest(Guid CourtId, DateOnly Date, int Hour);

public sealed record CreateBookingRequest(Guid VenueId, BookingSlotRequest[] Slots);

public sealed record BookingSlotResponse(
    Guid CourtId,
    string CourtName,
    DateOnly Date,
    int Hour,
    decimal BahtPerHour)
{
    /// <summary>
    /// A booking's hours as a reader sees them: in the order they are played, and named in the
    /// venue's own day rather than the reader's (PRD BR-10).
    /// </summary>
    public static BookingSlotResponse[] Of(
        Booking booking,
        IReadOnlyDictionary<Guid, string> courtNames) =>
        [
            .. booking.Slots
                .OrderBy(slot => slot.StartsAt)
                .ThenBy(slot => courtNames.GetValueOrDefault(slot.CourtId))
                .Select(slot =>
                {
                    var (date, hour) = Localization.PlatformRequirements.BangkokDateAndHour(
                        slot.StartsAt);
                    return new BookingSlotResponse(
                        slot.CourtId,
                        courtNames.GetValueOrDefault(slot.CourtId, string.Empty),
                        date,
                        hour,
                        slot.BahtPerHour);
                }),
        ];
}

/// <summary>
/// A booking as its booker sees it. The total and the slots are the snapshot (PRD BR-05), and
/// <see cref="HoldExpiresAt"/> is when the hold lapses if it has not been paid for (PRD BR-02).
/// </summary>
public sealed record BookingResponse(
    Guid Id,
    Guid VenueId,
    string VenueName,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset HoldExpiresAt,
    decimal TotalBaht,
    BookingSlotResponse[] Slots,
    /// <summary>When the booker last sent a slip, if they have (PRD US-04).</summary>
    DateTimeOffset? SlipUploadedAt,
    /// <summary>Whether the venue has the money, and what it owes back (PRD 6.2).</summary>
    string PaymentState,
    decimal RefundDueBaht,
    /// <summary>
    /// Of what is owed, how much has actually been sent back. Refunds are made outside the system
    /// and written down against the booking (PRD BR-06), which is US-18 — until then nothing has
    /// been recorded and this is nothing.
    /// </summary>
    decimal RefundedBaht,
    /// <summary>
    /// What had to arrive to hold these hours (PRD US-28). The same as the price unless the venue
    /// asked for a share of it up front, and then the difference is the desk's to collect.
    /// </summary>
    decimal DepositBaht,
    /// <summary>
    /// Why that much was asked for (PRD US-28), as a name the page turns into a sentence — the
    /// server does not send people words (US-23).
    /// </summary>
    string DepositReason,
    /// <summary>
    /// What is still owed at the venue, worked out by the server (PRD US-28, US-26). Nought
    /// unless the booking is one the venue is going to honour and is still short of its price —
    /// a cancelled booking has a difference too, and it is not something anybody owes.
    /// </summary>
    decimal ToPayBaht,
    /// <summary>What letting this booking go would mean right now (PRD US-05).</summary>
    CancellationOfferResponse Cancellation);

/// <summary>
/// How to pay for a booking that is waiting for money (PRD US-04). The payload is the string a
/// bank app reads out of the QR; the page turns it into squares and never builds it itself,
/// because the amount and the account in it are the server's to decide.
/// </summary>
public sealed record PaymentResponse(
    decimal TotalBaht,
    /// <summary>
    /// What has to arrive now to keep the hours (PRD US-28). The same as the price unless the
    /// venue asks for a share of it up front, and it is this amount — never the price — that the
    /// code carries, so that what a bank app fills in is what the venue is waiting for.
    /// </summary>
    decimal DepositBaht,
    /// <summary>Why that much, as a name the page turns into a sentence (PRD US-28, US-23).</summary>
    string DepositReason,
    /// <summary>What is left for the desk once the deposit has arrived. Zero when there is none.</summary>
    decimal PayAtVenueBaht,
    DateTimeOffset HoldExpiresAt,
    string AccountName,
    /// <summary>Null when the venue's account is not one a bank app would accept.</summary>
    string? PromptPayPayload);

/// <summary>
/// What the booker would get back for cancelling, worked out by the server so that the number
/// they are shown and the number they are given are the same one (PRD US-05, BR-06).
/// </summary>
public sealed record CancellationOfferResponse(
    bool Allowed,
    /// <summary>The share of the booking this would give back, which a tiered policy makes worth saying.</summary>
    int RefundPercent,
    decimal RefundBaht,
    /// <summary>The venue has yet to say whether the money arrived, so the amount is not settled.</summary>
    bool AwaitsVenue);

/// <summary>
/// A booker's own bookings, split where US-05 asks for the split: what is still ahead of them,
/// and what is behind. The server decides which is which, because it holds the clock that every
/// other answer about these bookings is given against (PRD 9.2).
/// </summary>
public sealed record BookingHistoryResponse(
    BookingResponse[] Upcoming,
    BookingResponse[] Past);

public static class BookingErrorCodes
{
    /// <summary>
    /// The arrival cannot move that way from where it is: it only goes forward, and checking
    /// somebody in needs a paid booking whose hours have not finished (PRD US-24).
    /// </summary>
    public const string ArrivalNotAllowed = "booking.arrival_not_allowed";

    public const string NoSlots = "booking.no_slots";
    public const string TooManySlots = "booking.too_many_slots";
    public const string DuplicateSlot = "booking.duplicate_slot";
    public const string MoreThanOneDay = "booking.more_than_one_day";
    public const string StartsTooSoon = "booking.starts_too_soon";

    /// <summary>
    /// The counter may book an hour that has started, but not one that is already over
    /// (PRD US-13): that is recording a game, not selling one.
    /// </summary>
    public const string HourAlreadyOver = "booking.hour_already_over";

    public const string InvalidCustomerName = "booking.invalid_customer_name";
    public const string InvalidCustomerPhone = "booking.invalid_customer_phone";
    public const string InvalidCounterPayment = "booking.invalid_counter_payment";
    public const string HourNotAvailable = "booking.hour_not_available";

    /// <summary>Someone else took one of the hours between reading the grid and confirming.</summary>
    public const string SlotJustTaken = "booking.slot_just_taken";

    /// <summary>
    /// This booking's hours are not something that can still change: it is a hold, or it is over
    /// (PRD US-29). An evening that has finished is answered with a booking of its own.
    /// </summary>
    public const string HoursCannotChange = "booking.hours_cannot_change";

    /// <summary>
    /// The hour they would run on into is somebody else's on that court. The refusal carries the
    /// courts that are free for it, because that is the next thing the counter asks (PRD US-29).
    /// </summary>
    public const string HourTaken = "booking.hour_taken";

    /// <summary>The court they would move to is not free for every hour being moved.</summary>
    public const string CourtNotFree = "booking.court_not_free";

    /// <summary>They are already on it, so there is nothing to move.</summary>
    public const string AlreadyOnThatCourt = "booking.already_on_that_court";

    /// <summary>No court of this venue has that id.</summary>
    public const string CourtUnknown = "booking.court_unknown";

    /// <summary>One held booking at a time, per booker (PRD S-22).</summary>
    public const string AlreadyHolding = "booking.already_holding";

    public const string NotFound = "booking.not_found";

    /// <summary>This booking is not in a state a booker may let go of (PRD 6.1).</summary>
    public const string NotCancellable = "booking.not_cancellable";

    /// <summary>The hours have begun. There is nothing left to give up (PRD 6.1).</summary>
    public const string PlayHasStarted = "booking.play_has_started";

    /// <summary>The venue has to say why, and pick from the reasons PRD 6.1 names.</summary>
    public const string ReasonRequired = "booking.reason_required";

    /// <summary>That reason does not describe hours that have already been played (PRD 6.1).</summary>
    public const string ReasonNotAllowedHere = "booking.reason_not_allowed_here";

    /// <summary>The venue has to say whether the money arrived; it is what settles the amount.</summary>
    public const string PaymentAnswerRequired = "booking.payment_answer_required";

    /// <summary>What was played may be corrected for a day, and this is past it (PRD 6.1).</summary>
    public const string TooLateToCorrect = "booking.too_late_to_correct";

    /// <summary>Nobody is a no-show a minute after the hour begins (PRD 6.1).</summary>
    public const string NotYetLateEnough = "booking.not_yet_late_enough";

    /// <summary>Correcting what was recorded after the hours is the owner's (PRD 6.1).</summary>
    public const string OwnerOnly = "booking.owner_only";

    /// <summary>The hours have been sold to somebody else since (PRD 6.1).</summary>
    public const string HoursAlreadyTaken = "booking.hours_already_taken";

    /// <summary>There is no unsettled payment on this booking to answer for.</summary>
    public const string NothingToSettle = "booking.nothing_to_settle";

    /// <summary>
    /// It moved between being read and being written — usually the venue deciding about the slip.
    /// What may be done with it now depends on where it has got to, so the answer is to look again.
    /// </summary>
    public const string ChangedMeanwhile = "booking.changed_meanwhile";
}

public static class SlipErrorCodes
{
    public const string NoFile = "slip.no_file";
    public const string TooLarge = "slip.too_large";
    public const string UnsupportedFile = "slip.unsupported_file";

    /// <summary>
    /// The hold ran out before the slip arrived. The booker may well have transferred the money,
    /// so what they are told has to point them at the venue (PRD US-04, 6.1).
    /// </summary>
    public const string HoldExpired = "slip.hold_expired";

    public const string NotAwaitingPayment = "slip.not_awaiting_payment";
    public const string NoSlip = "slip.none_uploaded";

    /// <summary>The venue looked at something that is no longer waiting to be looked at.</summary>
    public const string NotAwaitingVerification = "slip.not_awaiting_verification";

    /// <summary>
    /// The amount the venue read off the slip is not one this booking could have been paid: nought
    /// or less, or more than it still owes (PRD US-28).
    /// </summary>
    public const string InvalidAmount = "slip.invalid_amount";

    public const string ReasonRequired = "slip.reason_required";
    public const string ReasonTooLong = "slip.reason_too_long";
}

/// <summary>
/// Turning a booking away. Both answers are required: why, and whether the money arrived — the
/// second decides what goes back (PRD 6.1, 6.2).
/// </summary>
public sealed record RejectSlipRequest(
    string Reason,
    bool PaymentReceived,
    /// <summary>
    /// How much arrived, where the venue can see it is not what was asked for (PRD US-28). Null
    /// means what was asked for, which is what the booker's code carried.
    /// </summary>
    decimal? AmountBaht = null);

/// <summary>
/// One booking waiting for the venue to look at its slip (PRD US-12). It names the booker by the
/// address they signed up with — or, for a LINE account that has none, by the phone number they
/// gave instead (PRD US-01); nothing else about them is the venue's business here.
/// </summary>
public sealed record SlipQueueItemResponse(
    Guid BookingId,
    string? BookerEmail,
    string? BookerPhone,
    decimal TotalBaht,
    /// <summary>
    /// What the slip should be for (PRD US-28): the whole price unless the venue asks for a share
    /// of it up front, in which case the rest is the desk's to collect and the slip is not short.
    /// </summary>
    decimal DepositBaht,
    DateTimeOffset SlipUploadedAt,
    DateTimeOffset StartsAt,
    bool PlaysSoon,
    bool SameSlipSeenBefore);

/// <summary>
/// The venue turning a booking away (PRD US-13, 6.1). Which answers are needed depends on where
/// the booking stands: a hold needs none, one waiting to be checked needs to know whether the
/// money arrived, and one already paid for needs a reason.
/// </summary>
public sealed record VenueCancelRequest(string? Reason, bool? PaymentReceived, string? Note);

/// <summary>
/// The venue accepting a slip, and how much it says arrived (PRD US-12, US-28). Null means what
/// was asked for — which is the whole price unless the venue takes deposits, and is what the
/// booker's code was made out for either way. A number says the slip shows something else, which
/// a venue reading its own bank account is the only one who can tell.
/// </summary>
public sealed record ConfirmSlipRequest(decimal? AmountBaht);

/// <summary>The venue saying, at last, whether the money arrived (PRD US-13, 6.2).</summary>
public sealed record SettlePaymentRequest(bool PaymentReceived);

/// <summary>Taking back a no-show that was recorded wrongly. It has to say why (PRD 6.1).</summary>
public sealed record PlayedAfterAllRequest(string Reason);

/// <summary>
/// A booking taken at the counter for somebody standing at it (PRD US-13). The slots are the same
/// shape the online booking sends, so the grid that picks them is the same grid.
/// </summary>
public sealed record CounterBookingRequest(
    BookingSlotRequest[]? Slots,
    string? CustomerName,
    string? CustomerPhone,
    string? PaidBy);

/// <summary>
/// One of the venue's bookings for a day, as its counter reads it (PRD US-13). It names an online
/// booker by the address they signed up with, a counter customer by the name they gave, which is what the venue needs to find them, and
/// nothing else about them.
/// </summary>
public sealed record VenueBookingResponse(
    Guid BookingId,
    string? BookerEmail,
    /// <summary>Only when there is no address: a LINE booker is reached on the phone (US-01).</summary>
    string? BookerPhone,
    /// <summary>Online or at the counter, so the row can say who it is for (PRD US-13).</summary>
    string Channel,
    /// <summary>For a counter booking, the name and phone the customer gave; null otherwise.</summary>
    string? CustomerName,
    string? CustomerPhone,
    string Status,
    /// <summary>Whether they are coming, and then whether they came (PRD US-24).</summary>
    string Arrival,
    DateTimeOffset? ArrivedAt,
    /// <summary>When this booking stops being late and starts being a no-show (PRD US-24).</summary>
    DateTimeOffset GraceEndsAt,
    string PaymentState,
    decimal TotalBaht,
    /// <summary>What the venue has taken for this booking so far (PRD US-26).</summary>
    decimal TakenBaht,
    /// <summary>And what that leaves to take. Zero once it is paid for.</summary>
    decimal ToPayBaht,
    decimal RefundDueBaht,
    /// <summary>What the venue says it has sent back, and what that leaves (PRD 6.2, US-18).</summary>
    decimal SentBackBaht,
    decimal OutstandingBaht,
    BookingSlotResponse[] Slots,
    /// <summary>What each door the counter can press would come to, worked out by the server.</summary>
    VenueBookingActionsResponse Can);

/// <summary>
/// Which of the counter's doors are open on this booking right now, and what the money would do
/// if they were pressed (PRD US-13, 6.1). The page draws the buttons from this rather than from
/// rules of its own.
/// </summary>
public sealed record VenueBookingActionsResponse(
    bool Cancel,
    /// <summary>Writing down that they said they are coming (PRD US-24).</summary>
    bool ConfirmArrival,
    /// <summary>Taking them in at the desk.</summary>
    bool CheckIn,
    bool NoShow,
    bool SettlePayment,
    bool PlayedAfterAll,
    /// <summary>Taking money for it at the desk, in any form (PRD US-26).</summary>
    bool TakeMoney,
    /// <summary>Selling them the hour they would run on into (PRD US-29).</summary>
    bool Extend,
    /// <summary>Putting the hours they have not played on another court (PRD US-29).</summary>
    bool MoveCourt,
    /// <summary>
    /// The reasons this booking may be turned away for, and what each would owe the booker.
    /// Only the ones PRD 6.1 allows where it stands: a booking whose hours were played cannot be
    /// given back at the customer's request, so that answer is not offered.
    /// </summary>
    CancelChoiceResponse[] CancelChoices);

/// <summary>
/// A court that could take these hours, and what they would cost there. The amount is what the
/// hours being asked about come to at today's prices — it is only ever read for an hour being
/// added, because moving hours does not change what they cost (PRD US-29).
/// </summary>
public sealed record FreeCourtResponse(Guid CourtId, string CourtName, decimal? Baht);

/// <summary>The hour a booking would run on into, and where it could be played (PRD US-29).</summary>
public sealed record ExtendOptionResponse(
    DateOnly Date,
    int Hour,
    /// <summary>The court they are on, which is the one offered unless the venue says otherwise.</summary>
    Guid? SameCourtId,
    FreeCourtResponse[] Courts);

/// <summary>Where the hours a booking has not finished could be played instead (PRD US-29).</summary>
public sealed record MoveOptionResponse(int Hours, FreeCourtResponse[] Courts);

/// <summary>
/// What could still be done to this booking's hours. Either half is null where that door is shut
/// — a booking whose evening is over has neither.
/// </summary>
public sealed record BookingHoursResponse(ExtendOptionResponse? Extend, MoveOptionResponse? Move);

/// <summary>
/// One more hour. The court is the one they are on unless the venue names another, which is what
/// it does after being told the hour was taken there (PRD US-29).
/// </summary>
public sealed record ExtendBookingRequest(Guid? CourtId);

/// <summary>The court the hours that have not finished are to be played on instead.</summary>
public sealed record MoveCourtRequest(Guid CourtId);

/// <summary>
/// One answer the counter may give for turning a booking away, and the money it settles. The
/// amount rather than the share, because the share of a booking whose money never arrived is
/// still nothing (PRD 6.2) — and it is the amount the venue has to be sure about before it
/// presses (PRD US-13).
/// </summary>
public sealed record CancelChoiceResponse(string Reason, decimal RefundBaht);

/// <summary>Writing down a transfer the venue has already made (PRD US-18).</summary>
public sealed record RecordRefundRequest(
    decimal AmountBaht,
    DateOnly RefundedOn,
    string Method,
    string? Note);

/// <summary>Taking a record back. The owner may not do it without saying why (PRD US-18).</summary>
public sealed record VoidRefundRequest(string? Reason);

/// <summary>One transfer, as it was written down. Nothing about it changes afterwards.</summary>
public sealed record RefundRecordResponse(
    Guid Id,
    decimal AmountBaht,
    DateOnly RefundedOn,
    string Method,
    string? Note,
    DateTimeOffset RecordedAt,
    /// <summary>When it was taken back, if it was. A voided record still shows (PRD US-18).</summary>
    DateTimeOffset? VoidedAt,
    string? VoidReason);

/// <summary>
/// What a booking owes, what has been sent back, and what is left (PRD 6.2). The last of these is
/// the number a venue acts on: what it owes is a fact, what it still has to send is a job.
/// </summary>
public sealed record RefundsResponse(
    decimal RefundDueBaht,
    decimal SentBackBaht,
    decimal OutstandingBaht,
    RefundRecordResponse[] Records);

public static class RefundErrorCodes
{
    public const string AmountNotPositive = "refund.amount_not_positive";

    /// <summary>More than the booking still owes. Part payments are fine; overpaying is not.</summary>
    public const string MoreThanIsOwed = "refund.more_than_is_owed";

    /// <summary>A day in the future is not a transfer that has happened.</summary>
    public const string NotYetSent = "refund.not_yet_sent";

    public const string MethodNotAllowed = "refund.method_not_allowed";
    public const string NoteTooLong = "refund.note_too_long";
    public const string AlreadyVoided = "refund.already_voided";
}
