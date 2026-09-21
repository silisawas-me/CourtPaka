using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>One venue's line on the platform dashboard (PRD US-22).</summary>
public sealed record PlatformVenueFigures(
    Guid VenueId,
    string Code,
    string Name,
    string Status,
    int Bookings,
    int OnlineBookings,
    int StaffBookings,
    decimal GmvBaht);

public sealed record PlatformTotals(int Bookings, int OnlineBookings, int StaffBookings, decimal GmvBaht);

/// <summary>
/// The platform's figures for a range (PRD US-22). Commission collected and outstanding come with
/// the invoices of US-21, which do not exist yet, so they are not here rather than shown as zero.
/// </summary>
public sealed record PlatformDashboardResponse(
    DateOnly From,
    DateOnly To,
    /// <summary>How many venues are in each state now, whatever the range.</summary>
    Dictionary<string, int> VenuesByStatus,
    PlatformTotals Totals,
    PlatformVenueFigures[] Venues);

/// <summary>
/// What the platform reads about the venues trading on it (PRD US-22).
///
/// Counted the way a venue counts its own figures (US-15, PRD 6.2): by the day played, in
/// Bangkok. GMV is what venues kept from bookings made online — the counter's sales are the
/// venue's own business and the platform earns nothing on them (S-13).
/// </summary>
public static class PlatformDashboard
{
    /// <summary>The bookings that count as sold (PRD US-22).</summary>
    private static readonly BookingStatus[] Sold =
    [
        BookingStatus.Confirmed,
        BookingStatus.Completed,
        BookingStatus.NoShow,
    ];

    public static void MapPlatformDashboardEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/admin/dashboard", DashboardAsync)
            .WithTags("Platform")
            .RequireAuthorization(PlatformAdmins.PolicyName);
    }

    private static async Task<Results<Ok<PlatformDashboardResponse>, ProblemHttpResult>> DashboardAsync(
        DateOnly? from,
        DateOnly? to,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);
        var first = from ?? new DateOnly(today.Year, today.Month, 1);
        var last = to ?? first.AddMonths(1).AddDays(-1);

        if (last < first || last.DayNumber - first.DayNumber + 1 > VenueDashboard.MaxDays)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, DashboardErrorCodes.InvalidRange);
        }

        var response = await ReadAsync(
            database, first, last, timeProvider.GetUtcNow(), cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "platform_dashboard_viewed {From} {To}", first, last);

        return TypedResults.Ok(response);
    }

    public static async Task<PlatformDashboardResponse> ReadAsync(
        AppDbContext database,
        DateOnly first,
        DateOnly last,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var since = PlatformRequirements.BangkokHour(first, 0);
        var until = PlatformRequirements.BangkokHour(last.AddDays(1), 0);

        var venues = await database.Venues
            .AsNoTracking()
            .Select(venue => new { venue.Id, venue.Code, venue.Name, venue.Status })
            .ToListAsync(cancellationToken);

        // From the hours in the range, like a venue's own dashboard: a booking is found by any
        // hour inside it, then kept only if its first hour is (US-15).
        var touching = database.BookingSlots
            .Where(slot => slot.StartsAt >= since && slot.StartsAt < until)
            .Select(slot => slot.BookingId);

        var bookings = await database.Bookings
            .AsNoTracking()
            .Where(booking => touching.Contains(booking.Id))
            .Select(booking => new
            {
                booking.VenueId,
                booking.Status,
                booking.Channel,
                booking.PaymentState,
                booking.TotalBaht,
                booking.RefundDueBaht,
                FirstStart = booking.Slots.Min(slot => slot.StartsAt),
                LastEnd = booking.Slots.Max(slot => slot.EndsAt),
            })
            .ToListAsync(cancellationToken);

        var played = bookings
            .Where(booking => booking.FirstStart >= since && booking.FirstStart < until)
            .ToLookup(booking => booking.VenueId);

        var lines = venues
            .Select(venue =>
            {
                var mine = played[venue.Id].ToList();
                var sold = mine.Where(booking => Sold.Contains(booking.Status)).ToList();

                // What the venue kept from online bookings that have ended (PRD 6.2): money
                // received, the booking less what is owed back. A stored Confirmed counts once
                // its last hour is over, which is when it reads as Completed (PRD 9.2).
                var gmv = mine
                    .Where(booking =>
                        booking.Channel == BookingChannel.Online
                        && booking.PaymentState == PaymentState.Received
                        && (booking.Status is BookingStatus.Completed
                                or BookingStatus.NoShow
                                or BookingStatus.Cancelled
                            || (booking.Status == BookingStatus.Confirmed && booking.LastEnd <= now)))
                    .Sum(booking => booking.TotalBaht - booking.RefundDueBaht);

                return new PlatformVenueFigures(
                    venue.Id,
                    venue.Code,
                    venue.Name,
                    venue.Status.ToString(),
                    sold.Count,
                    sold.Count(booking => booking.Channel == BookingChannel.Online),
                    sold.Count(booking => booking.Channel == BookingChannel.Staff),
                    gmv);
            })
            // The venues with something to say first; the rest are still listed, so a venue
            // that sold nothing is visible as one.
            .OrderByDescending(line => line.GmvBaht)
            .ThenByDescending(line => line.Bookings)
            .ThenBy(line => line.Name)
            .ToArray();

        var byStatus = Enum.GetValues<VenueStatus>()
            .ToDictionary(
                status => status.ToString(),
                status => venues.Count(venue => venue.Status == status));

        return new PlatformDashboardResponse(
            first,
            last,
            byStatus,
            new PlatformTotals(
                lines.Sum(line => line.Bookings),
                lines.Sum(line => line.OnlineBookings),
                lines.Sum(line => line.StaffBookings),
                lines.Sum(line => line.GmvBaht)),
            lines);
    }
}
