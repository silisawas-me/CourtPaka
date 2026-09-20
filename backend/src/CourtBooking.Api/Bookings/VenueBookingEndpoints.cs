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
    public static void MapVenueBookingEndpoints(this RouteGroupBuilder venue)
    {
        var bookings = venue.MapGroup("/bookings");

        // Reading the day is any member's; changing what a booking says is not.
        bookings.MapGet("/", DayAsync).RequireAuthorization(VenuePolicies.Member);

        var manage = bookings.MapGroup("/{bookingId:guid}")
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageBookings));

        manage.MapPost("/cancel", CancelAsync);
        manage.MapPost("/no-show", NoShowAsync);
        manage.MapPost("/settle-payment", SettleAsync);
        manage.MapPost("/played", PlayedAfterAllAsync);
    }

    /// <summary>
    /// One day's bookings at this venue, in the order they are played. Everything that happened
    /// to the day is here, cancelled and written-off included: a counter looking back at last
    /// night needs to see what it decided as much as what it sold.
    /// </summary>
    private static async Task<Results<Ok<VenueBookingResponse[]>, ProblemHttpResult>> DayAsync(
        Guid venueId,
        DateOnly date,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var from = PlatformRequirements.BangkokHour(date, 0);
        var until = PlatformRequirements.BangkokHour(date.AddDays(1), 0);

        var day = await database.Bookings
            .AsNoTracking()
            .Include(booking => booking.Slots)
            .Include(booking => booking.CancellationPolicy!)
            .ThenInclude(policy => policy.Tiers)
            .Where(booking =>
                booking.VenueId == venueId
                && booking.Slots.Any(slot => slot.StartsAt >= from && slot.StartsAt < until))
            .Select(booking => new
            {
                Booking = booking,
                BookerEmail = booking.Booker!.Email,
            })
            .ToListAsync(cancellationToken);

        var courtNames = await database.Courts
            .Where(court => court.VenueId == venueId)
            .ToDictionaryAsync(court => court.Id, court => court.Name, cancellationToken);

        var answer = day
            .Select(row => Draw(row.Booking, row.BookerEmail, courtNames, now))
            .OrderBy(booking => booking.StartsAt)
            .ThenBy(booking => booking.BookerEmail)
            .ToArray();

        return TypedResults.Ok(answer);
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
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (request.Reason is not null && !Enum.TryParse<CancellationReason>(request.Reason, out _))
        {
            return ApiProblem.Of(
                StatusCodes.Status400BadRequest, BookingErrorCodes.ReasonNotAllowedHere);
        }

        var reason = request.Reason is null
            ? (CancellationReason?)null
            : Enum.Parse<CancellationReason>(request.Reason);

        return await DecideAsync(
            venueId,
            bookingId,
            BookingStatus.Cancelled,
            (booking, status, membership, now) => VenueDecisions.CancelOffer(
                booking, status, reason, request.PaymentReceived, IsOwner(membership), now),
            Recorded(request.Reason, request.Note),
            releaseHours: Release.All,
            venue,
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
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        DecideAsync(
            venueId,
            bookingId,
            BookingStatus.NoShow,
            (booking, status, _, now) => VenueDecisions.NoShowOffer(booking, status, now),
            reason: null,
            releaseHours: Release.WhatIsLeft,
            venue,
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
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        await DecideAsync(
            venueId,
            bookingId,
            BookingStatus.Completed,
            (booking, status, membership, now) => VenueDecisions.PlayedAfterAllOffer(
                booking, status, request.Reason, IsOwner(membership), now),
            Recorded(null, request.Reason),
            releaseHours: Release.None,
            venue,
            database,
            timeProvider,
            loggers,
            cancellationToken,
            // Taking the hours back means holding them again, and something else may be holding
            // them now. The database is the only place that knows (PRD BR-04).
            takesTheHoursBack: true);

    /// <summary>
    /// The venue says at last whether the money arrived, for a booking the booker gave up while
    /// it was still being checked. Until this, nobody knows what is owed (PRD US-13, 6.2).
    /// </summary>
    private static async Task<Results<Ok<VenueBookingResponse>, ProblemHttpResult>> SettleAsync(
        Guid venueId,
        Guid bookingId,
        SettlePaymentRequest request,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var booking = await ReadAsync(database, venueId, bookingId, cancellationToken);
        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        var offer = VenueDecisions.SettlementOffer(booking, request.PaymentReceived);
        if (offer.Refused is { } refused)
        {
            return Refusal(refused);
        }

        // The status does not move — only what is known about the money does, and with it what is
        // owed (PRD 6.2). The condition keeps two people answering at once from disagreeing.
        var settled = await database.Bookings
            .Where(candidate =>
                candidate.Id == bookingId
                && candidate.VenueId == venueId
                && candidate.PaymentState == PaymentState.Unconfirmed)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(candidate => candidate.PaymentState, offer.Payment)
                    .SetProperty(candidate => candidate.RefundDueBaht, offer.RefundBaht),
                cancellationToken);

        if (settled == 0)
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, BookingErrorCodes.ChangedMeanwhile);
        }

        AppEvents.For(loggers).LogInformation(
            "booking_payment_settled {BookingId} {VenueId} {PaymentState} {RefundDueBaht}",
            bookingId, venueId, offer.Payment, offer.RefundBaht);

        return TypedResults.Ok(await ReadDrawnAsync(database, venueId, bookingId, now, cancellationToken));
    }

    /// <summary>Which of a booking's hours a decision gives back.</summary>
    private enum Release
    {
        None,
        All,

        /// <summary>The ones that have not begun. What was played was played (PRD 6.1).</summary>
        WhatIsLeft,
    }

    /// <summary>
    /// Every door on this page is the same shape: read the booking, ask the rules what this would
    /// come to, refuse if they say no, then move the row, let go of the hours and write down who
    /// decided — all in one transaction, and only if the booking is still where it was read from.
    /// </summary>
    private static async Task<Results<Ok<VenueBookingResponse>, ProblemHttpResult>> DecideAsync(
        Guid venueId,
        Guid bookingId,
        BookingStatus decided,
        Func<Booking, BookingStatus, VenueMembership, DateTimeOffset, Cancellation.Offer> ask,
        string? reason,
        Release releaseHours,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken,
        bool takesTheHoursBack = false)
    {
        var now = timeProvider.GetUtcNow();
        var membership = venue.Require();

        var booking = await ReadAsync(database, venueId, bookingId, cancellationToken);
        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        // Two statuses, and the difference matters. What the booking reads as is what the rules
        // answer against — a confirmed booking whose hours are over is Completed to everyone
        // (PRD 9.2) — but the row still says what it says, so that is what the write has to find.
        var stored = booking.Status;
        var status = BookedSlots.StatusAt(booking, now);
        var offer = ask(booking, status, membership, now);
        if (offer.Refused is { } refused)
        {
            return Refusal(refused);
        }

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        // Slots before the booking row, which is the order every writer keeps (BookedSlots).
        if (releaseHours != Release.None)
        {
            await (releaseHours == Release.All
                ? BookedSlots.ReleaseAsync(database, bookingId, cancellationToken)
                : BookedSlots.ReleaseRemainingAsync(database, bookingId, now, cancellationToken));
        }

        if (takesTheHoursBack && !await BookedSlots.TakeBackAsync(
            database, bookingId, cancellationToken))
        {
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
            bookingId, status, decided, membership.UserId, now, reason));

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        Announce(loggers, decided, bookingId, venueId, offer);

        return TypedResults.Ok(
            await ReadDrawnAsync(database, venueId, bookingId, now, cancellationToken));
    }

    /// <summary>
    /// The name is part of the template, not a parameter, so a sink that groups by template counts
    /// these apart from every other move (PRD 8).
    /// </summary>
    private static void Announce(
        ILoggerFactory loggers,
        BookingStatus decided,
        Guid bookingId,
        Guid venueId,
        Cancellation.Offer offer)
    {
        var events = AppEvents.For(loggers);
        switch (decided)
        {
            case BookingStatus.Cancelled:
                events.LogInformation(
                    "booking_cancelled {BookingId} {VenueId} {By} {RefundDueBaht}",
                    bookingId, venueId, "venue", offer.RefundBaht);
                break;

            case BookingStatus.NoShow:
                events.LogInformation(
                    "booking_no_show {BookingId} {VenueId} {RefundDueBaht}",
                    bookingId, venueId, offer.RefundBaht);
                break;

            default:
                events.LogInformation(
                    "booking_completed {BookingId} {VenueId} {RefundDueBaht}",
                    bookingId, venueId, offer.RefundBaht);
                break;
        }
    }

    /// <summary>What the venue said, kept as one line of the booking's history (PRD 6.1).</summary>
    private static string? Recorded(string? reason, string? note)
    {
        var written = note?.Trim();
        var both = (reason, string.IsNullOrEmpty(written)) switch
        {
            (null, true) => null,
            (null, false) => written,
            (_, true) => reason,
            _ => $"{reason}: {written}",
        };

        return both?.Length > BookingStatusChange.ReasonMaxLength
            ? both[..BookingStatusChange.ReasonMaxLength]
            : both;
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

    private static Task<Booking?> ReadAsync(
        AppDbContext database,
        Guid venueId,
        Guid bookingId,
        CancellationToken cancellationToken) =>
        database.Bookings
            .AsNoTracking()
            .Include(booking => booking.Slots)
            .Include(booking => booking.CancellationPolicy!)
            .ThenInclude(policy => policy.Tiers)
            .SingleOrDefaultAsync(
                booking => booking.Id == bookingId && booking.VenueId == venueId,
                cancellationToken);

    private static async Task<VenueBookingResponse> ReadDrawnAsync(
        AppDbContext database,
        Guid venueId,
        Guid bookingId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var booking = await database.Bookings
            .AsNoTracking()
            .Include(candidate => candidate.Slots)
            .Include(candidate => candidate.CancellationPolicy!)
            .ThenInclude(policy => policy.Tiers)
            .Where(candidate => candidate.Id == bookingId && candidate.VenueId == venueId)
            .Select(candidate => new { Booking = candidate, Email = candidate.Booker!.Email })
            .SingleAsync(cancellationToken);

        var courtNames = await database.Courts
            .Where(court => court.VenueId == venueId)
            .ToDictionaryAsync(court => court.Id, court => court.Name, cancellationToken);

        return Draw(booking.Booking, booking.Email, courtNames, now);
    }

    private static VenueBookingResponse Draw(
        Booking booking,
        string? bookerEmail,
        IReadOnlyDictionary<Guid, string> courtNames,
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
            // Refunds are recorded against the booking by US-18; none have been.
            0m,
            booking.Slots.Min(slot => slot.StartsAt),
            booking.Slots.Max(slot => slot.EndsAt),
            [
                .. booking.Slots
                    .OrderBy(slot => slot.StartsAt)
                    .ThenBy(slot => courtNames.GetValueOrDefault(slot.CourtId))
                    .Select(slot =>
                    {
                        var (date, hour) = PlatformRequirements.BangkokDateAndHour(slot.StartsAt);
                        return new BookingSlotResponse(
                            slot.CourtId,
                            courtNames.GetValueOrDefault(slot.CourtId, string.Empty),
                            date,
                            hour,
                            slot.BahtPerHour);
                    }),
            ],
            Doors(booking, status, now));
    }

    /// <summary>
    /// Which doors are open, asked of the rules rather than restated here. Each is tried with the
    /// answers that only test the timing — the venue still has to give the real ones — so what the
    /// page draws and what the server will accept cannot drift apart.
    /// </summary>
    private static VenueBookingActionsResponse Doors(
        Booking booking,
        BookingStatus status,
        DateTimeOffset now)
    {
        var asOwner = VenueDecisions.CancelOffer(
            booking, status, CancellationReason.VenueInitiated, paymentReceived: false,
            byOwner: true, now);

        var onRequest = status == BookingStatus.Confirmed
            ? VenueDecisions.CancelOffer(
                booking, status, CancellationReason.CustomerRequest, paymentReceived: null,
                byOwner: true, now)
            : default;

        return new VenueBookingActionsResponse(
            asOwner.Allowed,
            VenueDecisions.NoShowOffer(booking, status, now).Allowed,
            booking.PaymentState == PaymentState.Unconfirmed,
            VenueDecisions
                .PlayedAfterAllOffer(booking, status, "recorded wrongly", byOwner: true, now)
                .Allowed,
            onRequest.RefundPercent);
    }
}
