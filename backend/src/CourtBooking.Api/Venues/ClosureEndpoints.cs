using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>
/// Shutting a court for a stretch of time, and opening it again (PRD US-11).
///
/// Its own permission — <c>CloseCourt</c>, which staff hold by default while the rest of the
/// venue's settings need <c>ManageSettings</c> (PRD US-14). A flooded floor is dealt with by
/// whoever is standing in front of it, not by whoever is allowed to change the prices.
///
/// The rule that matters: a closure may not be written while bookings stand inside it. The venue
/// is shown what is in the way and cancels those bookings itself, with a reason, because a
/// cancellation decides what goes back to the booker and this screen cannot make that call
/// (PRD 6.1, BR-06).
/// </summary>
public static class ClosureEndpoints
{
    public static void MapClosureEndpoints(this RouteGroupBuilder venue)
    {
        // Declared on each route rather than on the group: RequireAuthorization adds to whatever
        // it is called on and hands the same builder back, so asking the venue group for
        // CloseCourt would ask every endpoint under it for CloseCourt.
        var closing = VenuePolicies.Needs(VenuePermissions.CloseCourt);

        // Closing hangs off the court it shuts; reading and lifting are the venue's, because the
        // screen shows every court's closures together.
        venue.MapPost("/courts/{courtId:guid}/closures", CloseAsync).RequireAuthorization(closing);
        venue.MapGet("/closures", ListAsync).RequireAuthorization(closing);
        venue.MapPost("/closures/{closureId:guid}/lift", LiftAsync).RequireAuthorization(closing);
    }

    /// <summary>
    /// What is shut, and what was. Lifted closures come too: an evening with no bookings is
    /// explained by the closure that was in force at the time, not by the ones left today.
    /// </summary>
    private static async Task<Ok<CourtClosureResponse[]>> ListAsync(
        Guid venueId,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // Anything still to come, plus anything that has not long ended: a venue looking at this
        // screen is arranging the days ahead, not reading history.
        var from = PlatformRequirements.BangkokHour(
            PlatformRequirements.BangkokToday(timeProvider), 0);

        var closures = await OfVenue(database, venueId)
            .Where(closure => closure.EndsAt > from)
            .OrderBy(closure => closure.StartsAt)
            .ThenBy(closure => closure.Id)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(closures.Select(Drawn).ToArray());
    }

    private static async Task<Results<Ok<CourtClosureResponse>, ProblemHttpResult>> CloseAsync(
        Guid venueId,
        Guid courtId,
        CloseCourtRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);

        if (CourtValidation.ValidateClosure(
                request.StartsOn, request.StartHour, request.EndsOn, request.EndHour, today)
            is { } badRange)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, badRange);
        }

        if (CourtValidation.ValidateClosureReason(request.Reason) is { } badReason)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, badReason);
        }

        var court = await database.Courts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                one => one.Id == courtId && one.VenueId == venueId, cancellationToken);

        if (court is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, CourtErrorCodes.CourtNotFound);
        }

        var starts = PlatformRequirements.BangkokHour(request.StartsOn, request.StartHour);
        var ends = PlatformRequirements.BangkokHour(request.EndsOn, request.EndHour);
        var now = timeProvider.GetUtcNow();

        // Read before writing, and the write is what settles it: a booking taken between the two
        // is caught by the same check on the next attempt rather than silently shut over.
        var clashes = await ClashingAsync(database, court, starts, ends, now, cancellationToken);
        if (clashes.Length > 0)
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, CourtErrorCodes.BookingsInTheWay, "bookings", clashes);
        }

        var closure = new CourtClosure
        {
            CourtId = courtId,
            StartsAt = starts,
            EndsAt = ends,
            Reason = request.Reason.Trim(),
            CreatedByUserId = venue.Require().UserId,
            CreatedAt = now,
        };

        database.CourtClosures.Add(closure);
        await database.SaveChangesAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "court_closed {VenueId} {CourtId} {StartsAt} {EndsAt}",
            venueId, courtId, starts, ends);

        return TypedResults.Ok(Drawn(closure));
    }

    /// <summary>
    /// Opening the court again before the closure was due to end. Recorded on the row rather than
    /// by deleting it: why an evening had no bookings is part of the venue's history (PRD US-15).
    /// </summary>
    private static async Task<Results<Ok<CourtClosureResponse>, ProblemHttpResult>> LiftAsync(
        Guid venueId,
        Guid closureId,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Conditional, so two people lifting at once do not both write themselves onto the row.
        var lifted = await OfVenue(database, venueId)
            .Where(closure => closure.Id == closureId && closure.LiftedAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(closure => closure.LiftedAt, now)
                    .SetProperty(closure => closure.LiftedByUserId, venue.Require().UserId),
                cancellationToken);

        var after = await OfVenue(database, venueId)
            .SingleOrDefaultAsync(closure => closure.Id == closureId, cancellationToken);

        if (after is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, CourtErrorCodes.ClosureNotFound);
        }

        if (lifted == 0)
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, CourtErrorCodes.ClosureAlreadyLifted);
        }

        AppEvents.For(loggers).LogInformation(
            "court_reopened {VenueId} {CourtId} {ClosureId}", venueId, after.CourtId, closureId);

        return TypedResults.Ok(Drawn(after));
    }

    /// <summary>
    /// The bookings standing inside a stretch of time, as the venue needs to see them before it
    /// can shut the court. Held hours that have run out are not in the way of anything, so the
    /// same definition of "taken" the grid uses decides this too (PRD 9.2).
    /// </summary>
    internal static async Task<ClashingBookingResponse[]> ClashingAsync(
        AppDbContext database,
        Court court,
        DateTimeOffset starts,
        DateTimeOffset ends,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var slots = await BookedSlots.Active(database, now)
            .Where(slot => slot.CourtId == court.Id && slot.StartsAt < ends && slot.EndsAt > starts)
            .Select(slot => new
            {
                slot.BookingId,
                slot.StartsAt,
                slot.EndsAt,
                Status = slot.Booking!.Status,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. slots
                .GroupBy(slot => slot.BookingId)
                .Select(booking =>
                {
                    var (date, fromHour) = PlatformRequirements.BangkokDateAndHour(
                        booking.Min(slot => slot.StartsAt));
                    // Nothing runs past midnight yet, so the last hour of a booking either
                    // ends inside its own day or lands on hour 0 of the next one — which is the
                    // same moment as hour 24 of this one, and reads better as that.
                    var (_, toHour) = PlatformRequirements.BangkokDateAndHour(
                        booking.Max(slot => slot.EndsAt));

                    return new ClashingBookingResponse(
                        booking.Key,
                        court.Id,
                        court.Name,
                        date,
                        fromHour,
                        toHour == 0 ? CourtValidation.Midnight : toHour,
                        booking.First().Status.ToString());
                })
                .OrderBy(clash => clash.Date)
                .ThenBy(clash => clash.FromHour)
        ];
    }

    /// <summary>Every closure of a court this venue owns, and nothing of anybody else's.</summary>
    private static IQueryable<CourtClosure> OfVenue(AppDbContext database, Guid venueId) =>
        database.CourtClosures.Where(closure => closure.Court!.VenueId == venueId);

    private static CourtClosureResponse Drawn(CourtClosure closure)
    {
        var (startsOn, startHour) = PlatformRequirements.BangkokDateAndHour(closure.StartsAt);
        var (endsOn, endHour) = PlatformRequirements.BangkokDateAndHour(closure.EndsAt);

        return new CourtClosureResponse(
            closure.Id,
            closure.CourtId,
            startsOn,
            startHour,
            endsOn,
            endHour,
            closure.Reason,
            closure.CreatedAt,
            closure.LiftedAt);
    }
}
