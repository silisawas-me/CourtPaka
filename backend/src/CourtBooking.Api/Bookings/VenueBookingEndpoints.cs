using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// The counter's side of a booking somebody else made (PRD US-13). The booker's own way out is
/// <see cref="BookingEndpoints"/>; this is the one the venue reaches for when a customer rings up,
/// when nobody turns up, and when what was recorded about an evening turns out to be wrong.
///
/// Nothing here decides anything the venue has not said. Every door asks for the answers PRD 6.1
/// attaches to it, and refuses rather than guessing when one is missing.
/// </summary>
public static class VenueBookingEndpoints
{
    /// <summary>What one door does to the hours the booking holds.</summary>
    private enum Hours
    {
        /// <summary>Nothing. What was recorded about them is what changes.</summary>
        Keep,

        /// <summary>All of them, so the court is on sale again (PRD 6.1).</summary>
        ReleaseAll,

        /// <summary>The ones that have not begun. What was played was played.</summary>
        ReleaseRemaining,

        /// <summary>Claim them again, if nobody else has taken them since (PRD BR-04).</summary>
        TakeBack,
    }

    public static void MapVenueBookingEndpoints(this RouteGroupBuilder venue)
    {
        var bookings = venue.MapGroup("/bookings");

        // Reading the day is behind the permission too, not merely behind membership: it names
        // every booker by the address they signed up with, and PRD 8 gives that to the people
        // holding the permission rather than to the venue's members at large (PDPA, US-13).
        bookings.RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageBookings));

        bookings.MapGet("/", DayAsync);
        bookings.MapPost("/{bookingId:guid}/cancel", CancelAsync);
        bookings.MapPost("/{bookingId:guid}/no-show", NoShowAsync);
        bookings.MapPost("/{bookingId:guid}/settle-payment", SettleAsync);
        bookings.MapPost("/{bookingId:guid}/played", PlayedAfterAllAsync);

        bookings.MapRefundEndpoints();
    }

    /// <summary>
    /// One day's bookings at this venue, in the order they are played. Everything that happened
    /// to the day is here, cancelled and written-off included: a counter looking back at last
    /// night needs to see what it decided as much as what it sold.
    /// </summary>
    private static async Task<Ok<VenueBookingResponse[]>> DayAsync(
        Guid venueId,
        DateOnly date,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var from = PlatformRequirements.BangkokHour(date, 0);
        var until = PlatformRequirements.BangkokHour(date.AddDays(1), 0);

        var day = await ReadManyAsync(
            database,
            database.Bookings.Where(booking =>
                booking.VenueId == venueId
                && booking.Slots.Any(slot => slot.StartsAt >= from && slot.StartsAt < until)),
            venueId,
            IsOwner(venue.Require()),
            now,
            cancellationToken);

        return TypedResults.Ok(day);
    }

    /// <summary>
    /// The venue turns a booking away. What it has to say first depends on where the booking
    /// stands, and what it says decides what is owed (PRD 6.1, 6.2).
    /// </summary>
    private static async Task<Results<Ok<VenueBookingResponse>, ProblemHttpResult>> CancelAsync(
        Guid venueId,
        Guid bookingId,
        VenueCancelRequest request,
        CurrentVenue venue,
        VenueNotifications notifications,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        CancellationReason? reason = null;
        if (request.Reason is { } named)
        {
            // Only one of the three names, spelled as it is spelled. TryParse is looser than it
            // looks: it takes "7", and it takes "CustomerRequest,VenueInitiated" and ors them
            // into a third reason — which would apply that reason's money while the record said
            // something else entirely (PRD 6.1).
            if (!Enum.TryParse<CancellationReason>(named, out var parsed)
                || !Enum.GetNames<CancellationReason>().Contains(named, StringComparer.Ordinal))
            {
                return ApiProblem.Of(
                    StatusCodes.Status400BadRequest, BookingErrorCodes.ReasonNotAllowedHere);
            }

            reason = parsed;
        }

        // The reason goes to its own column as the value that was decided on, not as the
        // string that was sent: the record and the money have to say the same thing (PRD 6.1).
        if (Recorded(request.Note) is not { } recorded)
        {
            // The same column and the same refusal the slip queue gives, so the same code: a
            // reader who has learned what it means should not have to learn a second one.
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.ReasonTooLong);
        }

        var byOwner = IsOwner(venue.Require());

        return await DecideAsync(
            venueId,
            bookingId,
            BookingStatus.Cancelled,
            (booking, status, now) => VenueDecisions.CancelOffer(
                booking, status, reason, request.PaymentReceived, byOwner, now),
            recorded,
            reason,
            Hours.ReleaseAll,
            venue,
            notifications,
            database,
            timeProvider,
            loggers,
            cancellationToken);
    }

    /// <summary>
    /// Nobody turned up. The court was held for them, so nothing is owed — and the hours they
    /// have not reached yet go back on sale for whoever wants them (PRD 6.1).
    /// </summary>
    private static Task<Results<Ok<VenueBookingResponse>, ProblemHttpResult>> NoShowAsync(
        Guid venueId,
        Guid bookingId,
        CurrentVenue venue,
        VenueNotifications notifications,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        DecideAsync(
            venueId,
            bookingId,
            BookingStatus.NoShow,
            VenueDecisions.NoShowOffer,
            reason: null,
            cause: null,
            Hours.ReleaseRemaining,
            venue,
            notifications,
            database,
            timeProvider,
            loggers,
            cancellationToken);

    /// <summary>
    /// Somebody did turn up after all, and the no-show was a mistake. The owner's to undo, while
    /// the day is still fresh, and never once the hours have been sold to somebody else — which
    /// is why this one asks the database before it writes (PRD 6.1).
    /// </summary>
    private static async Task<Results<Ok<VenueBookingResponse>, ProblemHttpResult>> PlayedAfterAllAsync(
        Guid venueId,
        Guid bookingId,
        PlayedAfterAllRequest request,
        CurrentVenue venue,
        VenueNotifications notifications,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (Recorded(request.Reason) is not { } recorded)
        {
            // The same column and the same refusal the slip queue gives, so the same code: a
            // reader who has learned what it means should not have to learn a second one.
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.ReasonTooLong);
        }

        var byOwner = IsOwner(venue.Require());

        return await DecideAsync(
            venueId,
            bookingId,
            BookingStatus.Completed,
            (booking, status, now) => VenueDecisions.PlayedAfterAllOffer(
                booking, status, request.Reason, byOwner, now),
            recorded,
            cause: null,
            Hours.TakeBack,
            venue,
            notifications,
            database,
            timeProvider,
            loggers,
            cancellationToken);
    }

    /// <summary>
    /// The venue says at last whether the money arrived, for a booking the booker gave up while
    /// it was still being checked. Until this, nobody knows what is owed (PRD US-13, 6.2).
    /// </summary>
    private static async Task<Results<Ok<VenueBookingResponse>, ProblemHttpResult>> SettleAsync(
        Guid venueId,
        Guid bookingId,
        SettlePaymentRequest request,
        CurrentVenue venue,
        VenueNotifications notifications,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var booking = await OneAsync(database, venueId, bookingId).SingleOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        if (booking.PaymentState != PaymentState.Unconfirmed)
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, BookingErrorCodes.NothingToSettle);
        }

        // The share was written down when the booking ended; this is what turns it into an amount
        // (PRD 6.2). Nothing about where the booking stands changes.
        var payment = request.PaymentReceived ? PaymentState.Received : PaymentState.NotReceived;
        var refundDue = Refunds.DueFor(
            booking.Status, payment, booking.TotalBaht, booking.RefundPercent);

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        var settled = await database.Bookings
            .Where(candidate =>
                candidate.Id == bookingId
                && candidate.VenueId == venueId
                && candidate.PaymentState == PaymentState.Unconfirmed)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(candidate => candidate.PaymentState, payment)
                    .SetProperty(candidate => candidate.RefundDueBaht, refundDue),
                cancellationToken);

        if (settled == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, BookingErrorCodes.ChangedMeanwhile);
        }

        // Answering for money is a decision, and who made it is the first thing a complaint
        // asks (PRD 6.1).
        database.BookingStatusChanges.Add(BookingTransitions.Settled(
            bookingId, booking.Status, payment, venue.Require().UserId, now));

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "booking_payment_settled {BookingId} {VenueId} {PaymentState} {RefundDueBaht}",
            bookingId, venueId, payment, refundDue);

        // Saying the money did arrive is what turns the share into a debt, and nothing else would
        // say so — the count beside the door does not even change, because the booking only moves
        // from one half of it to the other (PRD US-17, BR-06).
        await notifications.MoneyMayBeWaitingAsync(venueId, bookingId, refundDue, payment);

        return TypedResults.Ok(
            await OneDrawnAsync(database, venueId, bookingId, venue, now, cancellationToken));
    }

    /// <summary>
    /// Every door on this page is the same shape: read the booking, ask the rules what this would
    /// come to, refuse if they say no, then let go of the hours, move the row and write down who
    /// decided — all in one transaction, and only if the booking is still where it was read from.
    /// </summary>
    private static async Task<Results<Ok<VenueBookingResponse>, ProblemHttpResult>> DecideAsync(
        Guid venueId,
        Guid bookingId,
        BookingStatus decided,
        Func<Booking, BookingStatus, DateTimeOffset, Cancellation.Offer> ask,
        string? reason,
        CancellationReason? cause,
        Hours hours,
        CurrentVenue venue,
        VenueNotifications notifications,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var membership = venue.Require();

        var booking = await OneAsync(database, venueId, bookingId).SingleOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        // Two statuses, and the difference matters. What the booking reads as is what the rules
        // answer against — a confirmed booking whose hours are over is Completed to everyone
        // (PRD 9.2) — but the row still says what it says, so that is what the write has to find.
        var stored = booking.Status;
        var status = BookedSlots.StatusAt(booking, now);
        var offer = ask(booking, status, now);
        if (offer.Refused is { } refused)
        {
            return Refusal(refused);
        }

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        // Slots before the booking row, which is the order every writer keeps (BookedSlots).
        switch (hours)
        {
            case Hours.ReleaseAll:
                await BookedSlots.ReleaseAsync(database, bookingId, cancellationToken);
                break;

            case Hours.ReleaseRemaining:
                await BookedSlots.ReleaseRemainingAsync(database, bookingId, now, cancellationToken);
                break;

            case Hours.TakeBack
                when !await BookedSlots.TakeBackAsync(database, bookingId, now, cancellationToken):
                await transaction.RollbackAsync(cancellationToken);
                return ApiProblem.Of(
                    StatusCodes.Status409Conflict, BookingErrorCodes.HoursAlreadyTaken);
        }

        var moved = await database.Bookings
            .Where(candidate =>
                candidate.Id == bookingId
                && candidate.VenueId == venueId
                && candidate.Status == stored)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(candidate => candidate.Status, decided)
                    .SetProperty(candidate => candidate.PaymentState, offer.Payment)
                    .SetProperty(candidate => candidate.RefundPercent, offer.RefundPercent)
                    .SetProperty(candidate => candidate.RefundDueBaht, offer.RefundBaht),
                cancellationToken);

        if (moved == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, BookingErrorCodes.ChangedMeanwhile);
        }

        // If the clock had already moved it and nothing had written that down, write it down
        // now — otherwise the history would skip a step and stop reading as a chain (PRD 6.1).
        if (status != stored)
        {
            database.BookingStatusChanges.Add(
                BookingTransitions.Record(bookingId, stored, status, null, now));
        }

        database.BookingStatusChanges.Add(BookingTransitions.Record(
            bookingId, status, decided, membership.UserId, now, reason, cause));

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Every move is announced, the clock's included: PRD 8 asks for one each time a booking
        // changes where it stands, not one per request.
        var events = AppEvents.For(loggers);
        if (status != stored)
        {
            events.LogInformation(
                BookingTransitions.EventTemplate(status), bookingId, venueId, 0m);
        }

        events.LogInformation(
            BookingTransitions.EventTemplate(decided), bookingId, venueId, offer.RefundBaht);

        // An amount owed back is a transfer somebody has to make by hand, outside this system
        // (BR-06), so the people who can make it are told there is one (PRD US-17).
        await notifications.MoneyMayBeWaitingAsync(
            venueId, bookingId, offer.RefundBaht, offer.Payment);

        return TypedResults.Ok(
            await OneDrawnAsync(database, venueId, bookingId, venue, now, cancellationToken));
    }

    /// <summary>
    /// What the venue wrote beside the reason, kept as one line of the booking's history
    /// (PRD 6.1). Null means it does not fit, which is a refusal, not a note to shorten.
    /// </summary>
    private static string? Recorded(string? note)
    {
        var written = note?.Trim() ?? string.Empty;

        // Refused rather than shortened, the way the slip queue refuses one: a record trimmed
        // without saying so is a record nobody can trust (PRD 6.1).
        return written.Length > BookingStatusChange.ReasonMaxLength ? null : written;
    }

    private static bool IsOwner(VenueMembership membership) => membership.Role == VenueRole.Owner;

    /// <summary>
    /// Which answer belongs to which refusal. A missing answer is the caller's mistake, a door
    /// that is the owner's is a matter of who is asking, and everything else is the booking
    /// having moved on.
    /// </summary>
    private static ProblemHttpResult Refusal(string code) => code switch
    {
        BookingErrorCodes.ReasonRequired
            or BookingErrorCodes.ReasonNotAllowedHere
            or BookingErrorCodes.PaymentAnswerRequired =>
            ApiProblem.Of(StatusCodes.Status400BadRequest, code),

        BookingErrorCodes.OwnerOnly => ApiProblem.Of(StatusCodes.Status403Forbidden, code),

        _ => ApiProblem.Of(StatusCodes.Status409Conflict, code),
    };

    private static IQueryable<Booking> OneAsync(
        AppDbContext database,
        Guid venueId,
        Guid bookingId) =>
        WithHours(
            database.Bookings.Where(
                booking => booking.Id == bookingId && booking.VenueId == venueId));

    /// <summary>
    /// A booking with what every rule here is measured against: the hours it holds, and the terms
    /// it was made under. Read without them, a booking cannot be spoken for at all.
    /// </summary>
    private static IQueryable<Booking> WithHours(IQueryable<Booking> bookings) =>
        bookings
            .AsNoTracking()
            .Include(booking => booking.Slots)
            .Include(booking => booking.CancellationPolicy!)
            .ThenInclude(policy => policy.Tiers);

    private static async Task<VenueBookingResponse> OneDrawnAsync(
        AppDbContext database,
        Guid venueId,
        Guid bookingId,
        CurrentVenue venue,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        (await ReadManyAsync(
            database,
            OneAsync(database, venueId, bookingId),
            venueId,
            IsOwner(venue.Require()),
            now,
            cancellationToken)).Single();

    /// <summary>
    /// The bookings a query selects, as the counter reads them. Court names belong to the venue
    /// rather than to a booking, so they are fetched once for the whole set.
    /// </summary>
    private static async Task<VenueBookingResponse[]> ReadManyAsync(
        AppDbContext database,
        IQueryable<Booking> bookings,
        Guid venueId,
        bool byOwner,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var found = await WithHours(bookings)
            .Select(booking => new { Booking = booking, Email = booking.Booker!.Email })
            .ToListAsync(cancellationToken);

        if (found.Count == 0)
        {
            return [];
        }

        var courtNames = await database.Courts
            .Where(court => court.VenueId == venueId)
            .ToDictionaryAsync(court => court.Id, court => court.Name, cancellationToken);

        var bookingIds = found.Select(row => row.Booking.Id).ToArray();
        var sentBack = await database.RefundRecords
            .StillStanding()
            .Where(record => bookingIds.Contains(record.BookingId))
            .GroupBy(record => record.BookingId)
            .Select(records => new
            {
                BookingId = records.Key,
                Baht = records.Sum(record => record.AmountBaht),
            })
            .ToDictionaryAsync(row => row.BookingId, row => row.Baht, cancellationToken);

        return
        [
            .. found
                .OrderBy(row => row.Booking.Slots.Min(slot => slot.StartsAt))
                .ThenBy(row => row.Email)
                .Select(row => Draw(
                    row.Booking,
                    row.Email,
                    courtNames,
                    byOwner,
                    sentBack.GetValueOrDefault(row.Booking.Id),
                    now)),
        ];
    }

    private static VenueBookingResponse Draw(
        Booking booking,
        string? bookerEmail,
        IReadOnlyDictionary<Guid, string> courtNames,
        bool byOwner,
        decimal sentBackBaht,
        DateTimeOffset now)
    {
        var status = BookedSlots.StatusAt(booking, now);

        return new VenueBookingResponse(
            booking.Id,
            bookerEmail,
            status.ToString(),
            booking.PaymentState.ToString(),
            booking.TotalBaht,
            booking.RefundDueBaht,
            sentBackBaht,
            Refunds.OutstandingOf(booking.RefundDueBaht, sentBackBaht),
            BookingSlotResponse.Of(booking, courtNames),
            Doors(booking, status, byOwner, now));
    }

    /// <summary>
    /// Which doors are open, asked of the rules rather than restated here — and asked as the
    /// person reading, so a member of staff is never shown a button the owner alone may press.
    /// Each is tried with the answers that only test the timing; the venue still gives the real
    /// ones when it presses.
    /// </summary>
    private static VenueBookingActionsResponse Doors(
        Booking booking,
        BookingStatus status,
        bool byOwner,
        DateTimeOffset now)
    {
        // Asked with an answer that only tests the timing and who is asking; the venue still
        // gives the real one when it presses.
        var cancelling = VenueDecisions.CancelOffer(
            booking, status, CancellationReason.VenueInitiated, paymentReceived: false,
            byOwner, now);

        return new VenueBookingActionsResponse(
            cancelling.Allowed,
            VenueDecisions.NoShowOffer(booking, status, now).Allowed,
            booking.PaymentState == PaymentState.Unconfirmed,
            VenueDecisions
                .PlayedAfterAllOffer(booking, status, "recorded wrongly", byOwner, now)
                .Allowed,
            cancelling.Allowed ? Choices(booking, status, byOwner, now) : []);
    }

    /// <summary>
    /// Every reason this booking may be turned away for, with what each owes back. Asked of the
    /// rules one by one rather than listed here, so a page can never offer an answer the server
    /// would refuse (PRD 6.1).
    /// </summary>
    private static CancelChoiceResponse[] Choices(
        Booking booking,
        BookingStatus status,
        bool byOwner,
        DateTimeOffset now) =>
        [
            .. Enum.GetValues<CancellationReason>()
                .Select(reason => (reason, offer: VenueDecisions.CancelOffer(
                    booking, status, reason, paymentReceived: null, byOwner, now)))
                .Where(choice => choice.offer.Allowed)
                .Select(choice => new CancelChoiceResponse(
                    choice.reason.ToString(), choice.offer.RefundBaht)),
        ];
}
