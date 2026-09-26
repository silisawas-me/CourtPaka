using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// The two things an evening asks for while it is being played (PRD US-29): one more hour, and a
/// different court. Neither makes a new booking and neither moves the booking's status — it is the
/// same evening, and the money for an added hour is owed on the booking that was already made.
///
/// Both write through the same index everything else books through: they queue for the court-hours
/// they want in the same order as everybody else, and the exclusion constraint is still the one
/// thing that says an hour is taken (PRD BR-04). Nothing here invents a second way to hold a court.
/// </summary>
internal static class BookingHourEndpoints
{
    public static void MapBookingHourEndpoints(this RouteGroupBuilder bookings)
    {
        // What is possible, so the counter can offer it without pressing anything. Reading changes
        // nothing, so it stays open to a suspended venue like the rest of this group (PRD US-20).
        bookings.MapGet("/{bookingId:guid}/hours", OptionsAsync);

        // An hour added is an hour sold, and selling is exactly what a suspension stops — so this
        // door says so where it is mapped, rather than inheriting the group's (PRD US-20).
        bookings.MapPost("/{bookingId:guid}/extend", ExtendAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageBookings));

        // Moving sells nothing: the hours are already theirs, and a court that has flooded still
        // floods at a venue the platform has stopped.
        bookings.MapPost("/{bookingId:guid}/move", MoveAsync);
    }

    /// <summary>
    /// What this booking's hours could still become: the hour it would run on into and the courts
    /// free for it, and the courts its remaining hours could be moved to.
    ///
    /// The counter is shown what the server would allow rather than working it out from the grid,
    /// because the grid is minutes old and a court that is free on it may not be. Both doors ask
    /// again when they are pressed; this only decides what to offer.
    /// </summary>
    private static async Task<Results<Ok<BookingHoursResponse>, ProblemHttpResult>> OptionsAsync(
        Guid venueId,
        Guid bookingId,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (await VenueBookingEndpoints.OneAsync(database, venueId, bookingId)
            .SingleOrDefaultAsync(cancellationToken) is not { } booking)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        var status = BookedSlots.StatusAt(booking, now);
        var today = PlatformRequirements.BangkokToday(timeProvider);

        ExtendOptionResponse? extend = null;
        if (BookingHours.NextHour(booking, status, now) is { } next
            && BookingHours.SameCourt(booking, status, now) is { } sameCourt)
        {
            var (date, hour) = PlatformRequirements.BangkokDateAndHour(next);
            var asked = new[] { new BookingSlotRequest(Guid.Empty, date, hour) };

            // The same window and the same clock the counter sells any other hour under
            // (PRD US-13). An evening that finished an hour ago has no next hour to offer.
            if (BookingValidation.Validate(asked, now, today, BookingChannel.Staff) is null)
            {
                var day = await VenueDay.LoadAsync(database, venueId, date, now, cancellationToken);
                extend = new ExtendOptionResponse(date, hour, sameCourt, FreeCourts(day, [hour]));
            }
        }

        MoveOptionResponse? move = null;
        var movable = BookingHours.Movable(booking, status, now);

        // Nothing is offered where nothing could be done with it: one court cannot take two
        // courts' worth of the same hour, and offering a court that the press would refuse is
        // worse than offering none.
        if (movable.Count > 0 && !BookingHours.OnTwoCourtsAtOnce(movable))
        {
            var hours = movable
                .Select(slot => PlatformRequirements.BangkokDateAndHour(slot.StartsAt))
                .ToList();

            var day = await VenueDay.LoadAsync(
                database, venueId, hours[0].Date, now, cancellationToken);

            // Only a court free for every hour being moved. Half a move is not on offer: the
            // players would be told to change court in the middle of the game they were moved for.
            // A court the booking is already on for those hours is not free for them, so the
            // day's own answer has already left it out.
            move = new MoveOptionResponse(
                movable.Count,
                FreeCourts(day, [.. hours.Select(one => one.Hour)], priced: false));
        }

        return TypedResults.Ok(new BookingHoursResponse(extend, move));
    }

    /// <summary>
    /// One more hour, on the court they are already on unless the venue names another (PRD US-29).
    ///
    /// It is priced at what that hour costs now, not at what the booking was sold for: the booking's
    /// snapshot answers for the hours it was made with (BR-05), and this hour is being sold today.
    /// The price is added to the booking's own total, which is what makes it something the desk
    /// collects (PRD US-26) — and a venue that had said it held all the money no longer does.
    /// </summary>
    private static async Task<Results<Ok<VenueBookingResponse>, ProblemHttpResult>> ExtendAsync(
        Guid venueId,
        Guid bookingId,
        ExtendBookingRequest? request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var membership = venue.Require();

        // A frozen venue is already kept out by the policy on this route. One still waiting to be
        // approved is not frozen, and it cannot sell an hour here that it could not sell at its
        // own counter (PRD US-10, US-20).
        if (venue.Status != VenueStatus.Approved)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, VenueErrorCodes.NotApproved);
        }

        if (await VenueBookingEndpoints.OneAsync(database, venueId, bookingId)
            .SingleOrDefaultAsync(cancellationToken) is not { } booking)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        var status = BookedSlots.StatusAt(booking, now);
        if (BookingHours.NextHour(booking, status, now) is not { } next
            || BookingHours.SameCourt(booking, status, now) is not { } sameCourt)
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, BookingErrorCodes.HoursCannotChange);
        }

        var courtId = request?.CourtId ?? sameCourt;
        var (date, hour) = PlatformRequirements.BangkokDateAndHour(next);
        var asked = new[] { new BookingSlotRequest(courtId, date, hour) };

        if (BookingValidation.Validate(
                asked, now, PlatformRequirements.BangkokToday(timeProvider), BookingChannel.Staff)
            is { } invalid)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        var slot = new BookingSlot
        {
            BookingId = bookingId,
            CourtId = courtId,
            StartsAt = next,
            EndsAt = next.AddHours(1),
            BahtPerHour = 0m,
            IsActive = true,
        };

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        // Queue for the hour before touching the index that guards it, in the one order everybody
        // takes: two people reaching for it meet here rather than inside the constraint, where
        // Postgres settles it by killing one of them (PRD BR-04).
        await BookedSlots.LockAsync(database, [slot], cancellationToken);

        var day = await ReadDayAsync(database, venueId, date, [courtId], now, cancellationToken);
        if (!day.Courts.Any(court => court.Id == courtId))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, BookingErrorCodes.CourtUnknown);
        }

        if (day.Status(courtId, hour) != HourStatus.Free || day.Price(courtId, hour) is not { } baht)
        {
            // Which courts are free instead, because that is the next thing the counter asks and
            // the answer is in the day already read (PRD US-29).
            return ApiProblem.Of(
                StatusCodes.Status409Conflict,
                BookingErrorCodes.HourTaken,
                "courts",
                FreeCourts(day, [hour]));
        }

        var extended = new BookingSlot
        {
            BookingId = bookingId,
            CourtId = courtId,
            StartsAt = slot.StartsAt,
            EndsAt = slot.EndsAt,
            BahtPerHour = baht,
            IsActive = true,
        };

        // The price joins the booking's total, which is what turns it into something owed at the
        // desk (PRD US-26). `Received` means the venue holds all of it (PRD US-28), and it no
        // longer does — so the payment state goes back to saying so and the desk's door reopens.
        //
        // Only where the receipts already account for the whole of what was owed, though. A
        // booking that says the money arrived and has no receipt saying how much is one from
        // before receipts were written down, and `Takings.HeldFor` reads it as holding what was
        // asked for. Moving it off `Received` would drop that to nothing — and a booking recorded
        // as holding nothing owes nothing back when it is cancelled. Flipping is allowed only
        // where it cannot change what the venue is recorded as holding.
        //
        // The money is read behind the same lock everything that reads receipts takes, and in
        // the same transaction as the write that follows (CLAUDE.md, PRD US-26).
        await CounterMoneyEndpoints.QueueForTheMoneyAsync(database, bookingId, cancellationToken);
        var taken = await CounterMoneyEndpoints.TakenAsync(database, bookingId, cancellationToken);

        if (booking.PaymentState == PaymentState.Received && taken < booking.TotalBaht)
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, BookingErrorCodes.HoursCannotChange);
        }

        var payment = booking.PaymentState == PaymentState.Received
            ? PaymentState.NotReceived
            : booking.PaymentState;

        var moved = await database.Bookings
            .Where(candidate =>
                candidate.Id == bookingId
                && candidate.VenueId == venueId
                && candidate.Status == booking.Status
                && candidate.PaymentState == booking.PaymentState
                // The price as it was when this hour was priced against it. Two people running
                // the same evening on to two different courts take different locks and would
                // otherwise both land, selling the booking one hour twice over and raising its
                // price twice; the second finds the total already moved and is told to look
                // again.
                && candidate.TotalBaht == booking.TotalBaht)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(candidate => candidate.TotalBaht, candidate => candidate.TotalBaht + baht)
                    .SetProperty(candidate => candidate.PaymentState, payment),
                cancellationToken);

        if (moved == 0)
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, BookingErrorCodes.ChangedMeanwhile);
        }

        database.BookingSlots.Add(extended);
        database.BookingSlotChanges.Add(new BookingSlotChange
        {
            BookingId = bookingId,
            What = HoursChange.Added,
            FromCourtId = null,
            ToCourtId = courtId,
            StartsAt = extended.StartsAt,
            BahtPerHour = baht,
            ChangedAt = now,
            ChangedByUserId = membership.UserId,
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (Exception failure) when (IsAlreadyTaken(failure))
        {
            // Somebody took the hour between the day being read and this write. The constraint is
            // the answer, not the read above (PRD BR-04).
            database.ChangeTracker.Clear();
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(
                StatusCodes.Status409Conflict,
                BookingErrorCodes.HourTaken,
                "courts",
                FreeCourts(
                    await ReadDayAsync(database, venueId, date, [], now, cancellationToken),
                    [hour]));
        }

        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "booking_hours_extended {BookingId} {VenueId} {CourtId} {StartsAt} {BahtPerHour}",
            bookingId, venueId, courtId, extended.StartsAt, baht);

        return TypedResults.Ok(
            await VenueBookingEndpoints.OneDrawnAsync(
                database, venueId, bookingId, venue, now, cancellationToken));
    }

    /// <summary>
    /// The same hours on a different court (PRD US-29) — a court that has flooded, a net that has
    /// gone. The price does not change: they are paying for the hours they bought, and where they
    /// are played is the venue's problem to solve, not theirs to pay for.
    ///
    /// Only the hours that have not finished. What was played was played somewhere, and moving that
    /// record would be rewriting where somebody stood.
    /// </summary>
    private static async Task<Results<Ok<VenueBookingResponse>, ProblemHttpResult>> MoveAsync(
        Guid venueId,
        Guid bookingId,
        MoveCourtRequest? request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var membership = venue.Require();

        if (await VenueBookingEndpoints.OneAsync(database, venueId, bookingId)
            .SingleOrDefaultAsync(cancellationToken) is not { } booking)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        var status = BookedSlots.StatusAt(booking, now);
        var movable = BookingHours.Movable(booking, status, now);
        if (movable.Count == 0)
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, BookingErrorCodes.HoursCannotChange);
        }

        if (request?.CourtId is not { } courtId)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, BookingErrorCodes.CourtUnknown);
        }

        if (BookingHours.OnTwoCourtsAtOnce(movable))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, BookingErrorCodes.HoursOverlap);
        }

        var moving = movable.Where(slot => slot.CourtId != courtId).ToList();
        if (moving.Count == 0)
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, BookingErrorCodes.AlreadyOnThatCourt);
        }

        // Grouped by the venue's own day, because a booking that has been run on past midnight
        // holds hours on two of them, and a day's read model only answers for its own (BR-10).
        var byDate = moving
            .Select(slot => PlatformRequirements.BangkokDateAndHour(slot.StartsAt))
            .GroupBy(when => when.Date)
            .ToDictionary(day => day.Key, day => day.Select(when => when.Hour).ToArray());

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        // Both ends of the move touch the index: the hours being let go of and the hours being
        // claimed. Queued together, in the one order, so this cannot deadlock against a booking
        // reaching for either (PRD BR-04).
        await BookedSlots.LockAsync(
            database,
            [.. moving, .. moving.Select(slot => new BookingSlot
            {
                BookingId = bookingId,
                CourtId = courtId,
                StartsAt = slot.StartsAt,
                EndsAt = slot.EndsAt,
                BahtPerHour = slot.BahtPerHour,
                IsActive = true,
            })],
            cancellationToken);

        foreach (var (date, hours) in byDate)
        {
            var day = await ReadDayAsync(
                database, venueId, date, [courtId], now, cancellationToken);
            if (!day.Courts.Any(court => court.Id == courtId))
            {
                return ApiProblem.Of(
                    StatusCodes.Status400BadRequest, BookingErrorCodes.CourtUnknown);
            }

            if (hours.Any(hour => day.Status(courtId, hour) != HourStatus.Free))
            {
                return ApiProblem.Of(
                    StatusCodes.Status409Conflict,
                    BookingErrorCodes.CourtNotFree,
                    "courts",
                    FreeCourts(day, hours, priced: false));
            }
        }

        var changedAt = now;

        try
        {
            foreach (var slot in moving)
            {
                // The row as it was read, or nothing: a booking cancelled a moment ago has let go
                // of these hours, and moving a released hour would claim a court for nobody.
                var moved = await database.BookingSlots
                    .Where(candidate =>
                        candidate.Id == slot.Id
                        && candidate.CourtId == slot.CourtId
                        && candidate.IsActive)
                    .ExecuteUpdateAsync(
                        set => set.SetProperty(candidate => candidate.CourtId, courtId),
                        cancellationToken);

                if (moved == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return ApiProblem.Of(
                        StatusCodes.Status409Conflict, BookingErrorCodes.ChangedMeanwhile);
                }

                database.BookingSlotChanges.Add(new BookingSlotChange
                {
                    BookingId = bookingId,
                    What = HoursChange.Moved,
                    FromCourtId = slot.CourtId,
                    ToCourtId = courtId,
                    StartsAt = slot.StartsAt,
                    BahtPerHour = slot.BahtPerHour,
                    ChangedAt = changedAt,
                    ChangedByUserId = membership.UserId,
                });
            }

            await database.SaveChangesAsync(cancellationToken);
        }
        // The day read above is advice; the constraint is the answer (PRD BR-04). Both shapes,
        // because the statement that can violate it is an ExecuteUpdate — which runs on its own
        // and raises the Postgres error bare, not wrapped the way SaveChanges wraps one.
        catch (Exception failure) when (IsAlreadyTaken(failure))
        {
            database.ChangeTracker.Clear();
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, BookingErrorCodes.CourtNotFree);
        }

        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "booking_hours_moved {BookingId} {VenueId} {CourtId} {Hours}",
            bookingId, venueId, courtId, moving.Count);

        return TypedResults.Ok(
            await VenueBookingEndpoints.OneDrawnAsync(
                database, venueId, bookingId, venue, now, cancellationToken));
    }

    /// <summary>
    /// The day as the database has it right now, with lapsed holds let go of first: the exclusion
    /// constraint reads only whether a slot is active, so a hold whose fifteen minutes are up
    /// would otherwise refuse an hour the rules say is free (PRD 9.2).
    ///
    /// The courts whose hours are being claimed are released first and by name; the rest of the
    /// day is read as it stands, which is all the offered list needs.
    /// </summary>
    private static async Task<VenueDay> ReadDayAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly date,
        IReadOnlyCollection<Guid> claiming,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await BookedSlots.ReleaseLapsedAsync(
            database,
            claiming,
            PlatformRequirements.BangkokHour(date, 0),
            PlatformRequirements.BangkokHour(date.AddDays(1), 0),
            now,
            cancellationToken);

        return await VenueDay.LoadAsync(database, venueId, date, now, cancellationToken);
    }

    /// <summary>
    /// The courts of this day that are free for every one of these hours, with what the hour costs
    /// on each. An hour with no price is not free to sell, whatever else the day says (PRD US-02).
    /// </summary>
    private static FreeCourtResponse[] FreeCourts(
        VenueDay day,
        IReadOnlyCollection<int> hours,
        bool priced = true) =>
        [
            .. day.Courts
                .Where(court => hours.All(hour =>
                    day.Status(court.Id, hour) == HourStatus.Free
                    && day.Price(court.Id, hour) is not null))
                .Select(court => new FreeCourtResponse(
                    court.Id,
                    court.Name,
                    // Null where the door does not change what anything costs: a court that has
                    // flooded is the venue's problem to solve, not the booker's to pay for.
                    priced ? hours.Sum(hour => day.Price(court.Id, hour) ?? 0m) : null)),
        ];

    /// <summary>
    /// Whether this failure is the no-overlap constraint refusing a row (PRD BR-04). It arrives
    /// in two shapes: wrapped, from <c>SaveChanges</c>, and bare, from an <c>ExecuteUpdate</c>,
    /// which runs a statement of its own.
    /// </summary>
    private static bool IsAlreadyTaken(Exception failure) => failure switch
    {
        PostgresException { SqlState: ExclusionViolation } => true,
        DbUpdateException { InnerException: PostgresException { SqlState: ExclusionViolation } } =>
            true,
        _ => false,
    };

    /// <summary>Postgres raises this when the no-overlap constraint refuses a row (PRD BR-04).</summary>
    private const string ExclusionViolation = "23P01";
}
