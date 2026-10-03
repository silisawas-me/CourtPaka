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

/// <summary>How many of today's bookings are of one kind (<see cref="BookingKinds"/>).</summary>
public sealed record KindCountResponse(string Kind, int Count);

/// <summary>Today at one of the venues a person may read the reports of (badPaka 2c).</summary>
public sealed record VenueTodayResponse(
    Guid VenueId,
    string Name,
    string Status,
    /// <summary>What today's played, paid-for bookings left the venue — the dashboard's rule (US-15).</summary>
    decimal KeptBaht,
    /// <summary>
    /// What the same weekday last week had left the venue by this time of day — the comparison
    /// the owner app puts under today's money, asked with the same rule at the same hour.
    /// </summary>
    decimal LastWeekKeptBaht,
    int Bookings,
    /// <summary>Every booking on today's floor that still stands, played or to come.</summary>
    int TodayBookings,
    /// <summary>Those, by kind: walk-in, standing group, package, app.</summary>
    KindCountResponse[] ByKind,
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
    decimal LastWeekKeptBaht,
    int Bookings,
    int TodayBookings,
    KindCountResponse[] ByKind,
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
            // Each venue's own day: one open until 02:00 is still on yesterday at 01:00 (thai-fit T4).
            var itsToday = VenueClock.Today(timeProvider, member.Venue!.DayStartsHour);
            today = itsToday < today ? itsToday : today;
            venues.Add(await ReadAsync(database, member.Venue!, itsToday, now, cancellationToken));
        }

        // No product event yet: PRD 8.1 names every event the code sends and this page is not in
        // it. Adding one is a line in the PRD first, which needs the owner's say (CLAUDE.md).

        return TypedResults.Ok(new OwnerTodayResponse(
            today,
            venues.Sum(venue => venue.KeptBaht),
            venues.Sum(venue => venue.LastWeekKeptBaht),
            venues.Sum(venue => venue.Bookings),
            venues.Sum(venue => venue.TodayBookings),
            ByKind(venues.SelectMany(venue => venue.ByKind)),
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

        // The same question a week ago, asked as it stood at this minute, so a morning is not
        // measured against a whole finished day.
        var (lastWeek, _, _) = await VenueDashboard.TodayAsync(
            database, venue.Id, today.AddDays(-7), now.AddDays(-7), cancellationToken);

        var (since, until) = VenueClock.Window(today, venue.DayStartsHour);

        // Today's bookings with their hours, read as the check-in door reads them. An hour that
        // runs into today counts: a game begun at a quarter to midnight is on the floor at ten
        // past, and still waiting to be taken in if nobody has.
        var onTheFloor = await database.Bookings
            .AsNoTracking()
            .Include(booking => booking.Slots)
            .Where(booking => booking.Slots.Any(slot =>
                slot.Court!.VenueId == venue.Id && slot.EndsAt > since && slot.StartsAt < until))
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

        var standing = onTheFloor
            .Where(booking => BookedSlots.StatusAt(booking, now)
                is not (BookingStatus.Cancelled or BookingStatus.Rejected or BookingStatus.Expired))
            .ToArray();

        var sellable = hours.Sum(one => one.Sellable);
        var booked = hours.Sum(one => one.Booked);

        return new VenueTodayResponse(
            venue.Id,
            venue.Name,
            venue.Status.ToString(),
            kept,
            lastWeek,
            bookings,
            standing.Length,
            ByKind(standing.Select(booking => new KindCountResponse(BookingKinds.Of(booking), 1))),
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

    /// <summary>Counts added up per kind, in the order the design lists them.</summary>
    private static KindCountResponse[] ByKind(IEnumerable<KindCountResponse> counts) =>
    [
        .. counts
            .GroupBy(one => one.Kind)
            .Select(kind => new KindCountResponse(kind.Key, kind.Sum(one => one.Count)))
            .OrderBy(kind => Array.IndexOf(Order, kind.Kind)),
    ];

    private static readonly string[] Order =
        [BookingKinds.WalkIn, BookingKinds.Series, BookingKinds.Package, BookingKinds.App];
}
