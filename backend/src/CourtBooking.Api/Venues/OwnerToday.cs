using System.Security.Claims;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>How one hour of today is going at one venue: court-hours on sale, and used.</summary>
public sealed record HourUseResponse(int Hour, int Sellable, int Booked);

/// <summary>Today at one of the venues a person may read the reports of (badPaka 2c).</summary>
public sealed record VenueTodayResponse(
    Guid VenueId,
    string Name,
    string Status,
    /// <summary>What today's played, paid-for bookings left the venue — the dashboard's rule (US-15).</summary>
    decimal KeptBaht,
    int Bookings,
    int SellableHours,
    int BookedHours,
    /// <summary>Null when nothing was on sale today, which is not the same as none of it used.</summary>
    decimal? UsedPercent,
    HourUseResponse[] Hours,
    /// <summary>Bookings the desk could take in right now — the check-in door's own rule (US-24).</summary>
    int DueNow,
    /// <summary>Of those, the ones whose wait has run out: the venue's call to make (US-24).</summary>
    int PastGrace,
    /// <summary>Courts shut this hour for a closure (US-11), by name.</summary>
    string[] ShutNow);

public sealed record OwnerTodayResponse(
    DateOnly Date,
    decimal KeptBaht,
    int Bookings,
    int DueNow,
    VenueTodayResponse[] Venues);

/// <summary>
/// Every venue a person may read the reports of, today, on one page (badPaka 2c). A person who
/// owns two venues asks how the evening is going at both, not at one and then the other.
/// </summary>
/// <remarks>
/// Each venue is asked the question its own dashboard asks, with the same door: a member without
/// <see cref="VenuePermissions.ViewReports"/> at a venue does not see that venue here, and a
/// suspended venue is still read, the same as its dashboard is (PRD US-20).
/// </remarks>
public static class OwnerToday
{
    public static void MapOwnerTodayEndpoints(this RouteGroupBuilder venues) =>
        venues.MapGet("/mine/today", TodayAsync);

    private static async Task<Ok<OwnerTodayResponse>> TodayAsync(
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var userId = CallerId.Of(principal);
        var now = timeProvider.GetUtcNow();
        var today = PlatformRequirements.BangkokToday(timeProvider);

        var memberships = await database.VenueMemberships
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Include(member => member.Venue)
            .OrderBy(member => member.Venue!.Name)
            .ToListAsync(cancellationToken);

        var readable = memberships
            .Where(member => VenuePermissionHandler.ReadsEvenWhenSuspended(
                member, member.Venue!.Status, VenuePermissions.ViewReports))
            .ToArray();

        var venues = new List<VenueTodayResponse>(readable.Length);
        foreach (var member in readable)
        {
            venues.Add(await ReadAsync(database, member.Venue!, today, now, cancellationToken));
        }

        // No product event yet: PRD 8.1 names every event the code sends and this page is not in
        // it. Adding one is a line in the PRD first, which needs the owner's say (CLAUDE.md).

        return TypedResults.Ok(new OwnerTodayResponse(
            today,
            venues.Sum(venue => venue.KeptBaht),
            venues.Sum(venue => venue.Bookings),
            venues.Sum(venue => venue.DueNow),
            [.. venues]));
    }

    private static async Task<VenueTodayResponse> ReadAsync(
        AppDbContext database,
        Venue venue,
        DateOnly today,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var (kept, bookings, hours) = await VenueDashboard.TodayAsync(
            database, venue.Id, today, now, cancellationToken);

        var since = PlatformRequirements.BangkokHour(today, 0);
        var until = PlatformRequirements.BangkokHour(today.AddDays(1), 0);

        // Today's bookings with their hours, read as the check-in door reads them.
        var onTheFloor = await database.Bookings
            .AsNoTracking()
            .Include(booking => booking.Slots)
            .Where(booking => booking.Slots.Any(slot =>
                slot.Court!.VenueId == venue.Id && slot.StartsAt >= since && slot.StartsAt < until))
            .ToListAsync(cancellationToken);

        var due = onTheFloor
            .Where(booking => Arrivals.CanCheckIn(booking, BookedSlots.StatusAt(booking, now), now))
            .ToArray();
        var pastGrace = due.Count(booking =>
            VenueDecisions.GraceEndsAt(booking.Slots.Min(slot => slot.StartsAt), venue.GraceMinutes) < now);

        // Shut at this instant: begun, not ended, and not lifted early (US-11).
        var shut = await database.CourtClosures
            .AsNoTracking()
            .Where(closure =>
                closure.Court!.VenueId == venue.Id
                && closure.StartsAt <= now
                && closure.EndsAt > now
                && (closure.LiftedAt == null || closure.LiftedAt > now))
            .Select(closure => closure.Court!.Name)
            .Distinct()
            .ToListAsync(cancellationToken);

        var sellable = hours.Sum(one => one.Sellable);
        var booked = hours.Sum(one => one.Booked);

        return new VenueTodayResponse(
            venue.Id,
            venue.Name,
            venue.Status.ToString(),
            kept,
            bookings,
            sellable,
            booked,
            sellable == 0
                ? null
                : Math.Round(100m * booked / sellable, 1, MidpointRounding.AwayFromZero),
            [.. hours.Select(one => new HourUseResponse(one.Hour, one.Sellable, one.Booked))],
            due.Length,
            pastGrace,
            [.. shut.Order()]);
    }
}
