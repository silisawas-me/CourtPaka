using System.Security.Claims;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>Joining the queue for a day that has nothing left (PRD US-27).</summary>
public sealed record JoinWaitlistRequest(
    Guid VenueId,
    DateOnly Date,
    int FromHour,
    int UntilHour,
    int Hours);

/// <summary>One place in a queue, as the person waiting reads it.</summary>
public sealed record WaitlistEntryResponse(
    Guid Id,
    Guid VenueId,
    string VenueName,
    DateOnly Date,
    int FromHour,
    int UntilHour,
    int Hours,
    string State,
    DateTimeOffset AskedAt);

/// <summary>
/// One place in a queue, as the venue reads it: who is waiting and what they asked for, so a
/// counter with an hour on its hands can pick up the phone (PRD US-27).
/// </summary>
public sealed record VenueWaitlistEntryResponse(
    Guid Id,
    /// <summary>Null once the booker has asked to be forgotten (PDPA, S-15).</summary>
    string? BookerEmail,
    string? BookerPhone,
    DateOnly Date,
    int FromHour,
    int UntilHour,
    int Hours,
    string State,
    DateTimeOffset AskedAt);

/// <summary>
/// The queue for a day a venue has already sold (PRD US-27).
///
/// It is deliberately a want rather than a booking: nothing is held, nothing is owed, and leaving
/// costs nothing. What it buys the venue is the phone number of somebody who wanted exactly the
/// hour that has just come back — which is the difference between an empty court and a sold one.
/// </summary>
public static class WaitlistEndpoints
{
    /// <summary>What a booker may do with their own place in a queue.</summary>
    public static void MapWaitlistEndpoints(this IEndpointRouteBuilder routes)
    {
        var waiting = routes.MapGroup("/waitlist").WithTags("Waitlist").RequireAuthorization();

        waiting.MapPost("/", JoinAsync);
        waiting.MapGet("/", MineAsync);
        waiting.MapDelete("/{entryId:guid}", LeaveAsync);
    }

    /// <summary>
    /// The queue as the venue sees it. Behind <c>ManageBookings</c> like the day's list, and for
    /// the same reason: it carries the addresses of people who are waiting (PDPA, US-13).
    /// </summary>
    public static void MapVenueWaitlistEndpoints(this RouteGroupBuilder venue) =>
        venue.MapGet("/waitlist", VenueQueueAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageBookings));

    /// <summary>
    /// Takes a place in the queue. It asks the same things of an account as a booking does — a
    /// queue that would ring somebody who cannot be rung is a queue with a hole in it — but it
    /// takes no hours and no money, so a booker may wait for a day they already hold hours on.
    /// </summary>
    private static async Task<Results<Created<WaitlistEntryResponse>, ProblemHttpResult>> JoinAsync(
        JoinWaitlistRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var bookerId = CallerId.Of(principal);
        var now = timeProvider.GetUtcNow();
        var today = PlatformRequirements.BangkokToday(timeProvider);

        if (Waitlist.Refusal(request.Date, request.FromHour, request.UntilHour, request.Hours, today)
            is { } wrong)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, wrong);
        }

        if (await PublicVenueEndpoints.ApprovedAsync(database, request.VenueId, cancellationToken)
            is not { } venue)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, VenueErrorCodes.NotFound);
        }

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        // Waiting is a promise that somebody will be rung, so it goes through the same gate as
        // every other tie a booker takes on (PRD S-15): a closed or suspended account is refused
        // here rather than found out when the hour comes free.
        if (await AccountGate.RefusalAsync(database, bookerId, cancellationToken) is { } refused)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, refused);
        }

        var booker = await database.Users
            .AsNoTracking()
            .Where(user => user.Id == bookerId)
            .Select(user => new
            {
                user.EmailConfirmed,
                user.PhoneNumber,
                SignsInWithLine = database.UserLogins.Any(login =>
                    login.UserId == user.Id && login.LoginProvider == LineLoginEndpoints.Provider),
            })
            .SingleOrDefaultAsync(cancellationToken);

        var missing = booker is null
            ? AuthErrorCodes.EmailNotVerified
            : BookingEligibility.MissingFor(
                booker.EmailConfirmed, booker.SignsInWithLine, booker.PhoneNumber);
        if (missing is not null)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, missing);
        }

        // One place per day per venue. Somebody who wants two windows on one day wants the wider
        // window, and a queue with the same name in it twice is a queue that offers twice.
        if (await database.WaitlistEntries.AnyAsync(
                entry => entry.VenueId == venue.Id
                    && entry.BookerUserId == bookerId
                    && entry.Date == request.Date
                    && (entry.State == WaitlistState.Waiting || entry.State == WaitlistState.Offered),
                cancellationToken))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, WaitlistErrorCodes.AlreadyWaiting);
        }

        var entry = new WaitlistEntry
        {
            VenueId = venue.Id,
            BookerUserId = bookerId,
            Date = request.Date,
            FromHour = request.FromHour,
            UntilHour = request.UntilHour,
            Hours = request.Hours,
            AskedAt = now,
        };

        database.WaitlistEntries.Add(entry);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "waitlist_joined {EntryId} {VenueId} {Date} {Hours}",
            entry.Id, venue.Id, request.Date, request.Hours);

        return TypedResults.Created(
            $"/api/waitlist/{entry.Id}", Draw(entry, venue.Name));
    }

    /// <summary>Everywhere this booker is waiting, soonest day first.</summary>
    private static async Task<Ok<WaitlistEntryResponse[]>> MineAsync(
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var bookerId = CallerId.Of(principal);
        var today = PlatformRequirements.BangkokToday(timeProvider);

        var mine = await database.WaitlistEntries
            .AsNoTracking()
            .Where(entry =>
                entry.BookerUserId == bookerId
                && entry.Date >= today
                && (entry.State == WaitlistState.Waiting || entry.State == WaitlistState.Offered))
            .OrderBy(entry => entry.Date)
            .ThenBy(entry => entry.FromHour)
            .Select(entry => new
            {
                Entry = entry,
                VenueName = entry.Venue!.Name,
            })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(
            mine.Select(one => Draw(one.Entry, one.VenueName)).ToArray());
    }

    /// <summary>
    /// Stands down. The row stays, because how long a queue was and how it emptied is the only
    /// way to answer "was it worth it" (PRD US-27), but it stops being a place in it.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> LeaveAsync(
        Guid entryId,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var bookerId = CallerId.Of(principal);

        // Conditional on it still being a place in the queue, so standing down twice — or
        // standing down while an offer is being written — does not rewrite an ending.
        var left = await database.WaitlistEntries
            .Where(entry =>
                entry.Id == entryId
                && entry.BookerUserId == bookerId
                && entry.State == WaitlistState.Waiting)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(entry => entry.State, WaitlistState.Gone)
                    .SetProperty(entry => entry.EndedAt, timeProvider.GetUtcNow()),
                cancellationToken);

        if (left == 0)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, WaitlistErrorCodes.NotFound);
        }

        AppEvents.For(loggers).LogInformation("waitlist_left {EntryId}", entryId);

        return TypedResults.NoContent();
    }

    /// <summary>Who is waiting on this venue, soonest day first.</summary>
    private static async Task<Ok<VenueWaitlistEntryResponse[]>> VenueQueueAsync(
        Guid venueId,
        DateOnly? date,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);

        var waiting = await database.WaitlistEntries
            .AsNoTracking()
            .Where(entry =>
                entry.VenueId == venueId
                && (date == null ? entry.Date >= today : entry.Date == date)
                && (entry.State == WaitlistState.Waiting || entry.State == WaitlistState.Offered))
            // The order the queue is offered in, which is the order it is shown in: first asked,
            // first rung (PRD US-27).
            .OrderBy(entry => entry.Date)
            .ThenBy(entry => entry.AskedAt)
            .Select(entry => new VenueWaitlistEntryResponse(
                entry.Id,
                entry.Booker!.DeletedAt == null ? entry.Booker.Email : null,
                entry.Booker.DeletedAt == null ? entry.Booker.PhoneNumber : null,
                entry.Date,
                entry.FromHour,
                entry.UntilHour,
                entry.Hours,
                entry.State.ToString(),
                entry.AskedAt))
            .ToArrayAsync(cancellationToken);

        return TypedResults.Ok(waiting);
    }

    private static WaitlistEntryResponse Draw(WaitlistEntry entry, string venueName) =>
        new(
            entry.Id,
            entry.VenueId,
            venueName,
            entry.Date,
            entry.FromHour,
            entry.UntilHour,
            entry.Hours,
            entry.State.ToString(),
            entry.AskedAt);
}
