using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>One day of a venue's dashboard (PRD US-15).</summary>
public sealed record DashboardDayResponse(
    DateOnly Date,
    decimal OnlineBaht,
    decimal StaffBaht,
    int SellableHours,
    int BookedHours);

/// <summary>One calendar month, from the days of it that fall inside the range asked for.</summary>
public sealed record DashboardMonthResponse(
    int Year,
    int Month,
    decimal OnlineBaht,
    decimal StaffBaht);

/// <summary>What is waiting for somebody at the venue, however far back it goes (PRD US-15).</summary>
public sealed record DashboardAttentionResponse(
    int SlipsToCheck,
    int PaymentsUnanswered,
    int RefundsOutstanding);

public sealed record DashboardResponse(
    DateOnly From,
    DateOnly To,
    decimal OnlineBaht,
    decimal StaffBaht,
    /// <summary>Confirmed and not started yet, whatever the range (PRD US-15).</summary>
    decimal AdvanceBaht,
    int SellableHours,
    int BookedHours,
    /// <summary>Null when the venue had nothing to sell in the range, rather than a made-up 0%.</summary>
    decimal? UtilizationPercent,
    DashboardDayResponse[] Days,
    DashboardMonthResponse[] Months,
    DashboardAttentionResponse Attention);

public static class DashboardErrorCodes
{
    public const string InvalidRange = "dashboard.invalid_range";
}

/// <summary>
/// A venue's own figures (PRD US-15, definitions in 6.2).
///
/// Everything is counted on the day it is played, in Bangkok, not on the day it was booked or
/// paid for. Money is what the venue keeps — the booking less what it owes back, not less what it
/// has sent back so far — so a number read last week does not move because a refund was written
/// down today.
/// </summary>
public static class VenueDashboard
{
    /// <summary>A year and a day: enough for "this year so far" plus one either side.</summary>
    public const int MaxDays = 366;

    /// <summary>The statuses that mean the hour was sold and kept (PRD US-15).</summary>
    private static readonly BookingStatus[] UsedStatuses =
    [
        BookingStatus.Confirmed,
        BookingStatus.Completed,
        BookingStatus.NoShow,
    ];

    public static void MapVenueDashboardEndpoints(this RouteGroupBuilder venue)
    {
        // Open at a suspended venue: a suspension stops the venue selling, not reading what it
        // already sold — and what it still owes back is on this page (PRD US-20).
        venue.MapGet("/dashboard", DashboardAsync)
            .RequireAuthorization(VenuePolicies.NeedsEvenWhenSuspended(VenuePermissions.ViewReports));
    }

    private static async Task<Results<Ok<DashboardResponse>, ProblemHttpResult>> DashboardAsync(
        Guid venueId,
        DateOnly? from,
        DateOnly? to,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        // This month, when nothing is asked for: the question an owner opens the page with.
        var today = PlatformRequirements.BangkokToday(timeProvider);
        var first = from ?? new DateOnly(today.Year, today.Month, 1);
        var last = to ?? first.AddMonths(1).AddDays(-1);

        if (last < first || last.DayNumber - first.DayNumber + 1 > MaxDays)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, DashboardErrorCodes.InvalidRange);
        }

        var now = timeProvider.GetUtcNow();
        var response = await ReadAsync(database, venueId, first, last, now, cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "venue_dashboard_viewed {VenueId} {From} {To}", venueId, first, last);

        return TypedResults.Ok(response);
    }

    public static async Task<DashboardResponse> ReadAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly first,
        DateOnly last,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var since = PlatformRequirements.BangkokHour(first, 0);
        var until = PlatformRequirements.BangkokHour(last.AddDays(1), 0);

        var kept = await KeptByDayAsync(database, venueId, since, until, now, cancellationToken);
        var (sellable, booked) = await HoursByDayAsync(
            database, venueId, first, last, since, until, cancellationToken);

        var days = Enumerable.Range(0, last.DayNumber - first.DayNumber + 1)
            .Select(offset => first.AddDays(offset))
            .Select(date =>
            {
                var money = kept.GetValueOrDefault(date);
                return new DashboardDayResponse(
                    date,
                    money.Online,
                    money.Staff,
                    sellable.GetValueOrDefault(date),
                    booked.GetValueOrDefault(date));
            })
            .ToArray();

        var months = days
            .GroupBy(day => (day.Date.Year, day.Date.Month))
            .Select(month => new DashboardMonthResponse(
                month.Key.Year,
                month.Key.Month,
                month.Sum(day => day.OnlineBaht),
                month.Sum(day => day.StaffBaht)))
            .ToArray();

        var sellableHours = days.Sum(day => day.SellableHours);
        var bookedHours = days.Sum(day => day.BookedHours);

        return new DashboardResponse(
            first,
            last,
            days.Sum(day => day.OnlineBaht),
            days.Sum(day => day.StaffBaht),
            await AdvanceAsync(database, venueId, now, cancellationToken),
            sellableHours,
            bookedHours,
            sellableHours == 0
                ? null
                : Math.Round(100m * bookedHours / sellableHours, 1, MidpointRounding.AwayFromZero),
            days,
            months,
            await AttentionAsync(database, venueId, cancellationToken));
    }

    /// <summary>
    /// What the venue keeps from each booking played in the range, by the day it is played and
    /// the channel it came through (PRD 6.2). Only a booking whose money arrived keeps anything,
    /// and only once it has ended — played, not turned up for, or cancelled.
    /// </summary>
    private static async Task<Dictionary<DateOnly, (decimal Online, decimal Staff)>> KeptByDayAsync(
        AppDbContext database,
        Guid venueId,
        DateTimeOffset since,
        DateTimeOffset until,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var bookings = await database.Bookings
            .AsNoTracking()
            .Where(booking =>
                booking.VenueId == venueId
                && booking.PaymentState == PaymentState.Received
                && (booking.Status == BookingStatus.Confirmed
                    || booking.Status == BookingStatus.Completed
                    || booking.Status == BookingStatus.NoShow
                    || booking.Status == BookingStatus.Cancelled)
                && booking.Slots.Min(slot => slot.StartsAt) >= since
                && booking.Slots.Min(slot => slot.StartsAt) < until)
            .Select(booking => new
            {
                booking.Status,
                booking.Channel,
                booking.TotalBaht,
                booking.RefundDueBaht,
                FirstStart = booking.Slots.Min(slot => slot.StartsAt),
                LastEnd = booking.Slots.Max(slot => slot.EndsAt),
            })
            .ToListAsync(cancellationToken);

        var byDay = new Dictionary<DateOnly, (decimal Online, decimal Staff)>();

        foreach (var booking in bookings)
        {
            // Confirmed is only kept once it has been played: stored Confirmed reads as Completed
            // after its last hour (PRD 9.2), and before that it is advance money, not revenue.
            if (booking.Status == BookingStatus.Confirmed && booking.LastEnd > now)
            {
                continue;
            }

            var keeps = booking.TotalBaht - booking.RefundDueBaht;
            var day = PlatformRequirements.BangkokDateAndHour(booking.FirstStart).Date;
            var (online, staff) = byDay.GetValueOrDefault(day);

            byDay[day] = booking.Channel == BookingChannel.Staff
                ? (online, staff + keeps)
                : (online + keeps, staff);
        }

        return byDay;
    }

    /// <summary>
    /// The court-hours the venue had to sell each day, and how many of them were used (PRD
    /// US-15). A court-hour counts once however many bookings touched it — a no-show whose hour
    /// was then sold to a walk-in is one hour used, not two — and only if it was for sale, so the
    /// share can never pass 100%.
    /// </summary>
    private static async Task<(Dictionary<DateOnly, int> Sellable, Dictionary<DateOnly, int> Booked)>
        HoursByDayAsync(
            AppDbContext database,
            Guid venueId,
            DateOnly first,
            DateOnly last,
            DateTimeOffset since,
            DateTimeOffset until,
            CancellationToken cancellationToken)
    {
        // The timelines once for the whole range, then each day is drawn from them in memory.
        var courts = await database.Courts
            .AsNoTracking()
            .Where(court => court.VenueId == venueId)
            .ToListAsync(cancellationToken);
        var statusChanges = await CourtEndpoints.StatusChangesAsync(
            database, venueId, last, cancellationToken);
        var schedules = await CourtEndpoints.SchedulesAsync(database, venueId, cancellationToken);
        var closures = await database.CourtClosures
            .AsNoTracking()
            .Where(closure =>
                closure.Court!.VenueId == venueId
                && closure.LiftedAt == null
                && closure.StartsAt < until
                && closure.EndsAt > since)
            .ToListAsync(cancellationToken);

        // Released slots count too: a no-show gives its hours back, but it used them (US-15).
        var used = await database.BookingSlots
            .AsNoTracking()
            .Where(slot =>
                slot.Court!.VenueId == venueId
                && slot.StartsAt >= since
                && slot.StartsAt < until
                && UsedStatuses.Contains(slot.Booking!.Status))
            .Select(slot => new { slot.CourtId, slot.StartsAt })
            .Distinct()
            .ToListAsync(cancellationToken);

        var usedByDay = used
            .Select(slot => (slot.CourtId, At: PlatformRequirements.BangkokDateAndHour(slot.StartsAt)))
            .ToLookup(slot => slot.At.Date, slot => (slot.CourtId, slot.At.Hour));

        var sellable = new Dictionary<DateOnly, int>();
        var booked = new Dictionary<DateOnly, int>();

        for (var date = first; date <= last; date = date.AddDays(1))
        {
            var day = VenueDay.Build(
                date,
                courts,
                statusChanges,
                closures,
                VenueTimeline.OpeningHoursOn(schedules, date),
                bands: [],
                taken: new HashSet<(Guid, int)>());

            sellable[date] = day.Courts.Sum(court => day.Hours.Count(hour => day.IsSellable(court.Id, hour)));
            booked[date] = usedByDay[date]
                .Distinct()
                .Count(slot => day.IsSellable(slot.CourtId, slot.Hour));
        }

        return (sellable, booked);
    }

    /// <summary>
    /// Confirmed bookings whose first hour has not started yet (PRD US-15). Asked from the hours
    /// ahead rather than from the bookings, because a played booking stays stored as Confirmed.
    /// </summary>
    private static async Task<decimal> AdvanceAsync(
        AppDbContext database,
        Guid venueId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ahead = await database.BookingSlots
            .AsNoTracking()
            .Where(slot =>
                slot.Court!.VenueId == venueId
                && slot.IsActive
                && slot.StartsAt > now
                && slot.Booking!.Status == BookingStatus.Confirmed
                && !slot.Booking.Slots.Any(earlier => earlier.StartsAt <= now))
            .Select(slot => new { slot.BookingId, slot.Booking!.TotalBaht })
            .Distinct()
            .ToListAsync(cancellationToken);

        return ahead.Sum(booking => booking.TotalBaht);
    }

    /// <summary>
    /// What is waiting (PRD US-15): slips nobody has checked, payments nobody has answered for,
    /// and money still to be sent back — counted by the same rules as the venue's notifications.
    /// </summary>
    private static async Task<DashboardAttentionResponse> AttentionAsync(
        AppDbContext database,
        Guid venueId,
        CancellationToken cancellationToken)
    {
        var mine = database.Bookings.Where(booking => booking.VenueId == venueId);

        var slips = await mine.CountAsync(
            booking => booking.Status == BookingStatus.PendingVerification, cancellationToken);

        var unanswered = await mine.CountAsync(
            booking => booking.PaymentState == PaymentState.Unconfirmed, cancellationToken);

        // The same rule as Refunds.StillStanding, written out: EF cannot translate a call that
        // builds a query when it sits inside a correlated subquery.
        var outstanding = await mine.CountAsync(
            booking => booking.RefundDueBaht > database.RefundRecords
                .Where(record => record.BookingId == booking.Id && record.VoidedAt == null)
                .Sum(record => record.AmountBaht),
            cancellationToken);

        return new DashboardAttentionResponse(slips, unanswered, outstanding);
    }
}
