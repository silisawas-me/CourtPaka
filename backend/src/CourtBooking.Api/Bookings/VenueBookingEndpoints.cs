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
    internal enum Hours
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
        // Still open at a suspended venue. Bookings taken before the suspension have to be
        // honoured or cancelled with a reason, and what is owed back still has to be sent —
        // none of which a venue can do through a door the platform has shut (PRD US-20).
        bookings.RequireAuthorization(
            VenuePolicies.NeedsEvenWhenSuspended(VenuePermissions.ManageBookings));

        bookings.MapGet("/", DayAsync);

        // Taking a booking is selling, which is exactly what a suspension stops (PRD US-20).
        // Declared on the route because this repo decides which side of a suspension an endpoint
        // is on where it is mapped: the group's policy lets a suspended venue in to finish old
        // business, and this one says this door is new business. The handler also refuses any
        // venue that is not approved — which is what covers one still waiting (US-10), and on its
        // own would cover a suspended one too. Both are kept; neither is the only thing standing.
        bookings.MapPost("/", TakeAtCounterAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageBookings));

        // Who is coming and who is here (PRD US-24). Both are things the counter writes down
        // about a booking it already has, so they stay open to a suspended venue like the rest of
        // this group: people still turn up to hours that were sold before the platform stopped it.
        bookings.MapPost("/{bookingId:guid}/confirm-arrival", ConfirmArrivalAsync);
        bookings.MapPost("/{bookingId:guid}/check-in", CheckInAsync);

        bookings.MapPost("/{bookingId:guid}/cancel", CancelAsync);
        bookings.MapPost("/{bookingId:guid}/no-show", NoShowAsync);
        bookings.MapPost("/{bookingId:guid}/settle-payment", SettleAsync);
        bookings.MapPost("/{bookingId:guid}/played", PlayedAfterAllAsync);

        // One more hour, and a different court (PRD US-29). Both declare their own side of a
        // suspension where they are mapped, so the group's answer is never the one that decides.
        bookings.MapBookingHourEndpoints();

        // Hours instead of money (PRD US-31). Selling, in the sense that matters: it settles a
        // booking, so a suspended venue may not do it.
        bookings.MapPost("/{bookingId:guid}/pay-with-package", PackageEndpoints.SpendAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageBookings));

        bookings.MapRefundEndpoints();
        // Money taken at the desk, in parts and in the form it arrived (PRD US-26).
        bookings.MapCounterMoneyEndpoints();
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
        if (BookingStatusChange.Recorded(request.Note) is not { } recorded)
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
            (booking, status, taken, now) => VenueDecisions.CancelOffer(
                booking, status, reason, request.PaymentReceived, byOwner, taken, now),
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
    /// The booker said they are coming — usually because somebody rang them (PRD US-24). It moves
    /// the arrival forward and nothing else: the booking, the money and the hours stay as they are.
    /// </summary>
    private static Task<Results<Ok<VenueBookingResponse>, ProblemHttpResult>> ConfirmArrivalAsync(
        Guid venueId,
        Guid bookingId,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        MoveArrivalAsync(
            venueId,
            bookingId,
            BookingArrival.Confirmed,
            (booking, status, _) => Arrivals.CanConfirm(booking, status),
            venue,
            database,
            timeProvider,
            loggers,
            cancellationToken);

    /// <summary>They are at the desk (PRD US-24). The hours were already theirs; this says so.</summary>
    private static Task<Results<Ok<VenueBookingResponse>, ProblemHttpResult>> CheckInAsync(
        Guid venueId,
        Guid bookingId,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        MoveArrivalAsync(
            venueId,
            bookingId,
            BookingArrival.Arrived,
            Arrivals.CanCheckIn,
            venue,
            database,
            timeProvider,
            loggers,
            cancellationToken);

    /// <summary>
    /// Writes one step of an arrival, with the row that says who wrote it (PRD US-24, PRD 8).
    ///
    /// It changes nothing else — not the status, not the money, not the hours — so unlike the
    /// counter's other doors it needs no transaction of its own: the move and its record are one
    /// save, which is what keeps them from parting.
    /// </summary>
    private static async Task<Results<Ok<VenueBookingResponse>, ProblemHttpResult>> MoveArrivalAsync(
        Guid venueId,
        Guid bookingId,
        BookingArrival to,
        Func<Booking, BookingStatus, DateTimeOffset, bool> allowed,
        CurrentVenue venue,
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

        var status = BookedSlots.StatusAt(booking, now);
        if (!allowed(booking, status, now))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, BookingErrorCodes.ArrivalNotAllowed);
        }

        var from = booking.Arrival;
        if (!ArrivalTransitions.Allowed(from, to))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, BookingErrorCodes.ArrivalNotAllowed);
        }

        // The booking was read without tracking, the way every rule here reads one, so the move
        // is a conditional update: it finds the arrival where this decision found it, or somebody
        // else moved it first and nothing is written twice.
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        var moved = await database.Bookings
            .Where(candidate =>
                candidate.Id == bookingId
                && candidate.VenueId == venueId
                && candidate.Arrival == from)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(candidate => candidate.Arrival, to)
                    .SetProperty(
                        candidate => candidate.ArrivedAt,
                        candidate => to == BookingArrival.Arrived ? now : candidate.ArrivedAt),
                cancellationToken);

        if (moved != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status409Conflict, BookingErrorCodes.ArrivalNotAllowed);
        }

        database.BookingArrivalChanges.Add(new BookingArrivalChange
        {
            BookingId = bookingId,
            From = from,
            To = to,
            ChangedAt = now,
            ChangedByUserId = membership.UserId,
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "booking_arrival_{Arrival} {BookingId}", to.ToString().ToLowerInvariant(), bookingId);

        return TypedResults.Ok(await OneDrawnAsync(database, venueId, bookingId, venue, now, cancellationToken));
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
            // The venue's own wait, which the rule takes rather than assumes (PRD US-24).
            (booking, status, _, now) =>
                VenueDecisions.NoShowOffer(booking, status, now, venue.GraceMinutes),
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
        if (BookingStatusChange.Recorded(request.Reason) is not { } recorded)
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
            (booking, status, _, now) => VenueDecisions.PlayedAfterAllOffer(
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

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        await CounterMoneyEndpoints.QueueForTheMoneyAsync(database, bookingId, cancellationToken);

        // The share was written down when the booking ended; this is what turns it into an amount
        // (PRD 6.2). Nothing about where the booking stands changes.
        var payment = request.PaymentReceived ? PaymentState.Received : PaymentState.NotReceived;
        var refundDue = Refunds.DueFor(
            booking.Status,
            payment,
            booking.TotalBaht,
            booking.RefundPercent,
            await CounterMoneyEndpoints.TakenAsync(database, bookingId, cancellationToken),
            booking.DepositBaht,
            booking.PackageBaht);

        var settled = await database.Bookings
            .Where(candidate =>
                candidate.Id == bookingId
                && candidate.VenueId == venueId
                && candidate.PaymentState == PaymentState.Unconfirmed
                // The price is no longer fixed once a booking is made: an evening that runs on
                // raises it (PRD US-29). Every amount below was worked out from the price read
                // before this transaction, so the write carries that price as its condition —
                // an hour added in between leaves this changing nothing, and the venue is told
                // to look again rather than settling money against a total that has moved.
                && candidate.TotalBaht == booking.TotalBaht)
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
            BookingTransitions.SettledTemplate, bookingId, venueId, payment, refundDue);

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
        Func<Booking, BookingStatus, decimal, DateTimeOffset, Cancellation.Offer> ask,
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

        if (await CloseAsync(
                venueId,
                bookingId,
                decided,
                ask,
                reason,
                cause,
                hours,
                venue.Require().UserId,
                now,
                notifications,
                database,
                loggers,
                cancellationToken) is { } refused)
        {
            return Refusal(refused);
        }

        return TypedResults.Ok(
            await OneDrawnAsync(database, venueId, bookingId, venue, now, cancellationToken));
    }

    /// <summary>
    /// One ending, written down: the hours let go of or taken back, the money worked out, the row
    /// moved under its own old status, and the history told about every step including the one the
    /// clock took (PRD 6.1). Answers null once it is written, or the code it was refused with.
    ///
    /// Apart from the endpoint so that something ending a whole standing arrangement can come
    /// through the same door, one week at a time and each on its own terms (PRD US-30). A second
    /// way of ending a booking would be a second answer about what is owed back.
    /// </summary>
    internal static async Task<string?> CloseAsync(
        Guid venueId,
        Guid bookingId,
        BookingStatus decided,
        Func<Booking, BookingStatus, decimal, DateTimeOffset, Cancellation.Offer> ask,
        string? reason,
        CancellationReason? cause,
        Hours hours,
        Guid byUserId,
        DateTimeOffset now,
        VenueNotifications notifications,
        AppDbContext database,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var booking = await OneAsync(database, venueId, bookingId).SingleOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            return BookingErrorCodes.NotFound;
        }

        // Two statuses, and the difference matters. What the booking reads as is what the rules
        // answer against — a confirmed booking whose hours are over is Completed to everyone
        // (PRD 9.2) — but the row still says what it says, so that is what the write has to find.
        var stored = booking.Status;
        var status = BookedSlots.StatusAt(booking, now);

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        // What the venue is holding, which is the ceiling on what any of these endings can give
        // back (PRD US-28). Behind the lock the desk takes, so a payment landing at this moment is
        // either counted here or waits for this to finish.
        await CounterMoneyEndpoints.QueueForTheMoneyAsync(database, bookingId, cancellationToken);

        var taken = await CounterMoneyEndpoints.TakenAsync(database, bookingId, cancellationToken);
        var offer = ask(booking, status, taken, now);
        if (offer.Refused is { } refused)
        {
            await transaction.RollbackAsync(cancellationToken);
            return refused;
        }

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
                return BookingErrorCodes.HoursAlreadyTaken;
        }

        var moved = await database.Bookings
            .Where(candidate =>
                candidate.Id == bookingId
                && candidate.VenueId == venueId
                && candidate.Status == stored
                // The price is no longer fixed once a booking is made: an evening that runs on
                // raises it (PRD US-29). Every amount below was worked out from the price read
                // before this transaction, so the write carries that price as its condition —
                // an hour added in between leaves this changing nothing, and the venue is told
                // to look again rather than settling money against a total that has moved.
                && candidate.TotalBaht == booking.TotalBaht)
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
            return BookingErrorCodes.ChangedMeanwhile;
        }

        // If the clock had already moved it and nothing had written that down, write it down
        // now — otherwise the history would skip a step and stop reading as a chain (PRD 6.1).
        if (status != stored)
        {
            database.BookingStatusChanges.Add(
                BookingTransitions.Record(bookingId, stored, status, null, now));
        }

        database.BookingStatusChanges.Add(BookingTransitions.Record(
            bookingId, status, decided, byUserId, now, reason, cause));

        // A booking a package paid for gives back hours, not money (PRD US-31, BR-06): the money
        // came in when the package was sold and is not the venue's to send anywhere. Written in
        // this transaction, because a booking cancelled with its hours left spent is a customer
        // out of pocket with nothing on the page to say so.
        PackageEndpoints.GiveHoursBack(database, booking, offer.RefundPercent, byUserId, now);

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

        return null;
    }

    private static bool IsOwner(VenueMembership membership) => membership.Role == VenueRole.Owner;

    /// <summary>
    /// Which answer belongs to which refusal. A missing answer is the caller's mistake, a booking
    /// this venue does not have is not here at all, a door that is the owner's is a matter of who
    /// is asking, and everything else is the booking having moved on.
    /// </summary>
    internal static ProblemHttpResult Refusal(string code) => code switch
    {
        BookingErrorCodes.NotFound => ApiProblem.Of(StatusCodes.Status404NotFound, code),

        BookingErrorCodes.ReasonRequired
            or BookingErrorCodes.ReasonNotAllowedHere
            or BookingErrorCodes.PaymentAnswerRequired =>
            ApiProblem.Of(StatusCodes.Status400BadRequest, code),

        BookingErrorCodes.OwnerOnly => ApiProblem.Of(StatusCodes.Status403Forbidden, code),

        _ => ApiProblem.Of(StatusCodes.Status409Conflict, code),
    };

    internal static IQueryable<Booking> OneAsync(
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

    /// <summary>
    /// A booking taken at the counter for somebody standing at it (PRD US-13). The customer needs
    /// no account; the counter writes down their name, a phone if they give one, and how they
    /// paid — and since they have paid, the booking starts confirmed.
    ///
    /// Everything that decides whether an hour can be had is the online path's own: the same
    /// read model prices the hours, the same locks queue for them, and the same exclusion
    /// constraint refuses one already taken. Only the clock differs — the counter may sell an hour
    /// that has started, because the person is already standing there (PRD US-13).
    /// </summary>
    private static async Task<Results<Created<VenueBookingResponse>, ProblemHttpResult>>
        TakeAtCounterAsync(
            Guid venueId,
            CounterBookingRequest request,
            CurrentVenue venue,
            AppDbContext database,
            TimeProvider timeProvider,
            ILoggerFactory loggers,
            CancellationToken cancellationToken)
    {
        var membership = venue.Require();

        // Frozen venues are already kept out by the policy. A venue still waiting to be approved
        // is not frozen, but it is not on the platform yet either, so it cannot sell anything
        // at a counter that it could not sell online (PRD US-10, US-20).
        if (venue.Status != VenueStatus.Approved)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, VenueErrorCodes.NotApproved);
        }

        var name = request.CustomerName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > Booking.CustomerNameMaxLength)
        {
            return ApiProblem.Of(
                StatusCodes.Status400BadRequest, BookingErrorCodes.InvalidCustomerName);
        }

        var phone = request.CustomerPhone?.Trim();
        if (!string.IsNullOrEmpty(phone)
            && (phone.Length > Booking.CustomerPhoneMaxLength
                || !phone.All(character => char.IsAsciiDigit(character) || character is '+' or '-' or ' ')))
        {
            return ApiProblem.Of(
                StatusCodes.Status400BadRequest, BookingErrorCodes.InvalidCustomerPhone);
        }

        // Hours instead of money (PRD US-31). Where a package is named nothing is going in the
        // till, so there is no way of paying to read.
        var onHours = request.PackageId is not null;

        // The names and nothing else: Enum.TryParse would take "1" and "Cash,Transfer".
        if (!onHours
            && (request.PaidBy is not { } named
                || !Enum.GetNames<CounterPayment>().Contains(named, StringComparer.Ordinal)))
        {
            return ApiProblem.Of(
                StatusCodes.Status400BadRequest, BookingErrorCodes.InvalidCounterPayment);
        }

        var paidBy = onHours ? null : request.PaidBy;
        var now = timeProvider.GetUtcNow();
        var slots = request.Slots ?? [];

        if (BookingValidation.Validate(
                slots, now, PlatformRequirements.BangkokToday(timeProvider), BookingChannel.Staff)
            is { } invalid)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        var priced = await BookingEndpoints.PriceSlotsAsync(
            database, venueId, slots, now, loggers, cancellationToken);
        if (priced.Error is { } unavailable)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, unavailable);
        }

        var policyId = await BookingEndpoints.InForcePolicyIdAsync(
            database, venueId, cancellationToken);

        // The hours have to be there before the court is put aside, and they have to still be
        // there when it is: the check and the write are one transaction, taken in the same order
        // and behind the same lock as every other way of paying (PRD US-26).
        HourPackage? package = null;
        if (request.PackageId is { } packageId)
        {
            package = await database.HourPackages
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    one => one.Id == packageId && one.VenueId == venueId, cancellationToken);

            if (package is null)
            {
                return ApiProblem.Of(StatusCodes.Status404NotFound, PackageErrorCodes.NotFound);
            }

            if (package.ExpiredAt is not null
                || package.ExpiresOn < PlatformRequirements.BangkokToday(timeProvider))
            {
                return ApiProblem.Of(StatusCodes.Status409Conflict, PackageErrorCodes.RunOut);
            }

            if (await PackageEndpoints.BalanceAsync(database, package.Id, cancellationToken)
                < priced.Slots.Length)
            {
                return ApiProblem.Of(
                    StatusCodes.Status409Conflict, PackageErrorCodes.NotEnoughHours);
            }
        }

        var booking = package is null
            ? Booking.AtCounter(
                venueId,
                name,
                string.IsNullOrEmpty(phone) ? null : phone,
                Enum.Parse<CounterPayment>(paidBy!),
                policyId,
                priced.Slots,
                membership.UserId,
                now)
            : Booking.OnHours(
                venueId,
                name,
                string.IsNullOrEmpty(phone) ? null : phone,
                package,
                priced.Slots.Length,
                policyId,
                priced.Slots,
                membership.UserId,
                now);

        if (await BookingEndpoints.WriteNewAsync(database, booking, timeProvider, cancellationToken)
            is { } refused)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, refused);
        }

        if (package is null)
        {
            // The money was in the venue's hands before the booking was written, which is why it
            // starts confirmed (PRD US-13) — so the day's count is told about it too (US-26).
            database.PaymentReceipts.Add(new PaymentReceipt
            {
                BookingId = booking.Id,
                VenueId = venueId,
                AmountBaht = booking.TotalBaht,
                Method = paidBy == nameof(CounterPayment.Cash)
                    ? PaymentMethod.Cash
                    : PaymentMethod.PromptPay,
                ReceivedAt = now,
                ReceivedByUserId = membership.UserId,
            });
        }
        else
        {
            // Nothing in the till: the money came in when the package was sold, and a receipt
            // here would count it a second time on a day it did not arrive (PRD US-31).
            database.PackageEntries.Add(new PackageEntry
            {
                PackageId = package.Id,
                Hours = -booking.PackageHours,
                Move = PackageMove.Used,
                BookingId = booking.Id,
                At = now,
                ByUserId = membership.UserId,
            });
        }

        await database.SaveChangesAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            BookingTransitions.EventName(BookingStatus.Confirmed)
            + " {BookingId} {VenueId} {Channel} {Slots} {TotalBaht}",
            booking.Id,
            venueId,
            booking.Channel,
            booking.Slots.Count,
            booking.TotalBaht);

        return TypedResults.Created(
            $"/api/venues/{venueId}/bookings/{booking.Id}",
            await OneDrawnAsync(database, venueId, booking.Id, venue, now, cancellationToken));
    }

    /// <summary>One booking as the counter reads it, doors and all. Shared with the money the
    /// counter takes for it (PRD US-26), which changes the same row.</summary>
    internal static async Task<VenueBookingResponse> OneDrawnAsync(
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
            // A booker who asked to be forgotten is shown as nobody, not as a placeholder address.
            .Select(booking => new
            {
                Booking = booking,
                Email = booking.Booker!.DeletedAt == null ? booking.Booker.Email : null,
                // A LINE booker may have no address at all; the number they gave is how the
                // venue reaches them then (PRD US-01).
                Phone = booking.Booker.DeletedAt == null ? booking.Booker.PhoneNumber : null,
            })
            .ToListAsync(cancellationToken);

        if (found.Count == 0)
        {
            return [];
        }

        var courtNames = await database.Courts
            .Where(court => court.VenueId == venueId)
            .ToDictionaryAsync(court => court.Id, court => court.Name, cancellationToken);

        // How long this venue waits for somebody before they have not come (PRD US-24).
        var graceMinutes = await database.Venues
            .Where(venue => venue.Id == venueId)
            .Select(venue => venue.GraceMinutes)
            .SingleAsync(cancellationToken);

        var bookingIds = found.Select(row => row.Booking.Id).ToArray();

        // What each of them has been paid so far, counted from the receipts (PRD US-26).
        var taken = await database.PaymentReceipts
            .Where(receipt => receipt.BookingId != null && bookingIds.Contains(receipt.BookingId.Value))
            .GroupBy(receipt => receipt.BookingId!.Value)
            .Select(receipts => new
            {
                BookingId = receipts.Key,
                Baht = receipts.Sum(receipt => receipt.AmountBaht),
            })
            .ToDictionaryAsync(row => row.BookingId, row => row.Baht, cancellationToken);
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
                    row.Phone,
                    courtNames,
                    byOwner,
                    sentBack.GetValueOrDefault(row.Booking.Id),
                    taken.GetValueOrDefault(row.Booking.Id),
                    graceMinutes,
                    now)),
        ];
    }

    private static VenueBookingResponse Draw(
        Booking booking,
        string? bookerEmail,
        string? bookerPhone,
        IReadOnlyDictionary<Guid, string> courtNames,
        bool byOwner,
        decimal sentBackBaht,
        decimal takenBaht,
        int graceMinutes,
        DateTimeOffset now)
    {
        var status = BookedSlots.StatusAt(booking, now);
        var startsAt = booking.Slots.Min(slot => slot.StartsAt);
        var outstanding = Takings.OutstandingOf(booking.TotalBaht, takenBaht);

        return new VenueBookingResponse(
            booking.Id,
            bookerEmail,
            bookerEmail is null ? bookerPhone : null,
            booking.Channel.ToString(),
            booking.CustomerName,
            booking.CustomerPhone,
            status.ToString(),
            booking.Arrival.ToString(),
            booking.ArrivedAt,
            VenueDecisions.GraceEndsAt(startsAt, graceMinutes),
            booking.PaymentState.ToString(),
            booking.TotalBaht,
            takenBaht,
            outstanding,
            booking.RefundDueBaht,
            sentBackBaht,
            Refunds.OutstandingOf(booking.RefundDueBaht, sentBackBaht),
            BookingSlotResponse.Of(booking, courtNames),
            Doors(booking, status, byOwner, takenBaht, outstanding, graceMinutes, now));
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
        decimal takenBaht,
        decimal outstandingBaht,
        int graceMinutes,
        DateTimeOffset now)
    {
        // Asked with an answer that only tests the timing and who is asking; the venue still
        // gives the real one when it presses.
        var cancelling = VenueDecisions.CancelOffer(
            booking, status, CancellationReason.VenueInitiated, paymentReceived: false,
            byOwner, takenBaht, now);

        return new VenueBookingActionsResponse(
            cancelling.Allowed,
            Arrivals.CanConfirm(booking, status),
            Arrivals.CanCheckIn(booking, status, now),
            VenueDecisions.NoShowOffer(booking, status, now, graceMinutes).Allowed,
            booking.PaymentState == PaymentState.Unconfirmed,
            VenueDecisions
                .PlayedAfterAllOffer(booking, status, "recorded wrongly", byOwner, now)
                .Allowed,
            Takings.CanTake(status, booking.PaymentState, outstandingBaht),
            // An hour to run on into, and hours that have not been played yet (PRD US-29). Both
            // are only whether the door is open — which court, and whether one is free, is the
            // hours endpoint's answer, because it depends on the rest of the day.
            BookingHours.SameCourt(booking, status, now) is not null,
            BookingHours.Movable(booking, status, now) is { Count: > 0 } movable
                && !BookingHours.OnTwoCourtsAtOnce(movable),
            cancelling.Allowed ? Choices(booking, status, byOwner, takenBaht, now) : []);
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
        decimal takenBaht,
        DateTimeOffset now) =>
        [
            .. Enum.GetValues<CancellationReason>()
                .Select(reason => (reason, offer: VenueDecisions.CancelOffer(
                    booking, status, reason, paymentReceived: null, byOwner, takenBaht, now)))
                .Where(choice => choice.offer.Allowed)
                .Select(choice => new CancelChoiceResponse(
                    choice.reason.ToString(), choice.offer.RefundBaht)),
        ];
}
