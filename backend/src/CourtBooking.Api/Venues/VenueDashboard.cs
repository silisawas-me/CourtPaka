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
    int BookedHours,
    /// <summary>Bookings counted in the money above: paid for, played, on this day.</summary>
    int Bookings,
    /// <summary>What those bookings left the venue owing back (PRD 6.2).</summary>
    decimal RefundDueBaht,
    /// <summary>What the venue has written down as sent back so far (PRD US-18).</summary>
    decimal RefundedBaht);

/// <summary>One calendar month, from the days of it that fall inside the range asked for.</summary>
public sealed record DashboardMonthResponse(
    int Year,
    int Month,
    decimal OnlineBaht,
    decimal StaffBaht,
    int Bookings,
    decimal RefundDueBaht,
    decimal RefundedBaht);

/// <summary>What is waiting for somebody at the venue, however far back it goes (PRD US-15).</summary>
public sealed record DashboardAttentionResponse(
    int SlipsToCheck,
    int PaymentsUnanswered,
    int RefundsOutstanding);

/// <summary>
/// Hours the venue had sold and then lost, and how many of them it sold again (PRD US-27).
///
/// Lost means an hour somebody had taken and gave back: cancelled by either side, turned away
/// for a slip that was not good, or written off as a no-show. A hold that ran out is not in it —
/// nobody ever bought those, and counting every abandoned pick as a loss would bury the number
/// that matters.
/// </summary>
public sealed record DashboardRecoveryResponse(
    int HoursLost,
    /// <summary>Of those, the ones somebody else has now taken.</summary>
    int HoursRefilled,
    /// <summary>What the hours that came back were sold for.</summary>
    decimal RefilledBaht,
    /// <summary>The ones the queue itself filled, which is what a queue is for (PRD US-27).</summary>
    int HoursFromQueue,
    decimal FromQueueBaht);

/// <summary>
/// Hours the venue has been paid for and not yet given (PRD US-31, ⚠️ S-27).
///
/// It is not revenue and it is not in the figures above: the money arrived when the packages were
/// sold, and what the venue has in exchange for it is an obligation to let somebody on a court.
/// It is here so that obligation is a number somebody can see rather than a surprise on a quiet
/// Tuesday when four groups turn up having already paid.
///
/// Not tied to the range. What is owed is owed today, whatever month is being read.
/// </summary>
public sealed record DashboardOwedHoursResponse(
    int Hours,
    /// <summary>What those hours were paid for, at what each package charged for an hour.</summary>
    decimal Baht,
    /// <summary>How many packages they are spread across.</summary>
    int Packages,
    /// <summary>Of those, the ones whose hours are about to run out (⚠️ S-28).</summary>
    int RunningOut);

/// <summary>
/// What the counter sold besides court time, and what the venue paid out (PRD US-32, US-33).
///
/// Kept apart from the court money above because it is a different business with a different
/// margin — a venue that cannot tell the two apart cannot tell whether either is working. What
/// is left over is the plainest subtraction there is, and it is labelled as that: it is not
/// profit in any sense an accountant would sign, which is what S-30 goes to them with.
/// </summary>
public sealed record DashboardTradeResponse(
    /// <summary>What was rung up at the counter, less what was taken back.</summary>
    decimal ShopBaht,
    /// <summary>What the venue wrote down as paid out, less what was voided.</summary>
    decimal SpentBaht,
    /// <summary>Court money plus shop money, less what was paid out. Not profit (⚠️ S-30).</summary>
    decimal LeftOverBaht);

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
    DashboardAttentionResponse Attention,
    DashboardRecoveryResponse Recovery,
    /// <summary>Hours sold and not yet given (PRD US-31). Never part of the money above.</summary>
    DashboardOwedHoursResponse OwedHours,
    /// <summary>The counter's other trade, and the money that went out (PRD US-32, US-33).</summary>
    DashboardTradeResponse Trade);

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

    /// <summary>
    /// The statuses that mean an hour somebody had was given back (PRD US-27). Expired is not
    /// among them: a hold that ran out was never sold.
    /// </summary>
    private static readonly BookingStatus[] LostStatuses =
    [
        BookingStatus.Cancelled,
        BookingStatus.Rejected,
        BookingStatus.NoShow,
    ];

    /// <summary>
    /// The statuses that mean somebody has the hour again (PRD US-27). A fresh hold is not one:
    /// it has fifteen minutes to become real, and an hour counted as recovered that then lapses
    /// is a number that walks backwards.
    /// </summary>
    private static readonly BookingStatus[] RefilledStatuses =
    [
        BookingStatus.PendingVerification,
        BookingStatus.Confirmed,
        BookingStatus.Completed,
        BookingStatus.NoShow,
    ];

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
        var (first, last) = PlatformRequirements.MonthOr(from, to, timeProvider);

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
                    booked.GetValueOrDefault(date),
                    money.Bookings,
                    money.RefundDue,
                    money.Refunded);
            })
            .ToArray();

        var months = days
            .GroupBy(day => (day.Date.Year, day.Date.Month))
            .Select(month => new DashboardMonthResponse(
                month.Key.Year,
                month.Key.Month,
                month.Sum(day => day.OnlineBaht),
                month.Sum(day => day.StaffBaht),
                month.Sum(day => day.Bookings),
                month.Sum(day => day.RefundDueBaht),
                month.Sum(day => day.RefundedBaht)))
            .ToArray();

        var sellableHours = days.Sum(day => day.SellableHours);
        var bookedHours = days.Sum(day => day.BookedHours);
        var onlineBaht = days.Sum(day => day.OnlineBaht);
        var staffBaht = days.Sum(day => day.StaffBaht);

        return new DashboardResponse(
            first,
            last,
            onlineBaht,
            staffBaht,
            await AdvanceAsync(database, venueId, now, cancellationToken),
            sellableHours,
            bookedHours,
            sellableHours == 0
                ? null
                : Math.Round(100m * bookedHours / sellableHours, 1, MidpointRounding.AwayFromZero),
            days,
            months,
            await AttentionAsync(database, venueId, cancellationToken),
            await RecoveryAsync(database, venueId, since, until, cancellationToken),
            await OwedHoursAsync(database, venueId, now, cancellationToken),
            await TradeAsync(
                database, venueId, first, last, since, until, onlineBaht + staffBaht,
                cancellationToken));
    }

    /// <summary>
    /// What the counter sold and what the venue paid out over the range (PRD US-32, US-33).
    ///
    /// Both are counted on the day they happened — a sale on the day it was rung up, an expense
    /// on the day the venue says the money left — rather than on a day of service, because
    /// neither is a booking and neither has one.
    /// </summary>
    private static async Task<DashboardTradeResponse> TradeAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly first,
        DateOnly last,
        DateTimeOffset since,
        DateTimeOffset until,
        decimal courtBaht,
        CancellationToken cancellationToken)
    {
        // Everything rung up in the range, less what was handed back in it — netted on the day
        // the money moved rather than by dropping the sale outright. A June sale taken back in
        // July is June's takings and July's refund: leaving it out of June instead would change
        // a month that had already been read, and would disagree with the drawer, which counts
        // the money going out on the day it went (PRD US-32).
        var rungUp = await database.ShopSales
            .AsNoTracking()
            .Where(sale =>
                sale.VenueId == venueId
                && sale.SoldAt >= since
                && sale.SoldAt < until)
            .SumAsync(sale => (decimal?)sale.TotalBaht, cancellationToken) ?? 0m;

        var handedBack = await database.ShopSales
            .AsNoTracking()
            .Where(sale =>
                sale.VenueId == venueId
                && sale.CancelledAt >= since
                && sale.CancelledAt < until)
            .SumAsync(sale => (decimal?)sale.TotalBaht, cancellationToken) ?? 0m;

        var shop = rungUp - handedBack;

        var spent = await database.Spends
            .AsNoTracking()
            .Where(spend =>
                spend.VenueId == venueId
                && spend.VoidedAt == null
                && spend.PaidOn >= first
                && spend.PaidOn <= last)
            .SumAsync(spend => (decimal?)spend.AmountBaht, cancellationToken) ?? 0m;

        return new DashboardTradeResponse(
            shop,
            spent,
            decimal.Round(courtBaht + shop - spent, 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// What the venue still owes in hours (PRD US-31). Counted from the movements, like every
    /// other answer about a package: a balance nobody writes down is a balance that cannot drift.
    ///
    /// Packages whose hours have been written off are not in it — the day passed, and what the
    /// venue owed stopped being owed (⚠️ S-28, and what happens to the money then is the question
    /// S-27 goes to the accountant with).
    /// </summary>
    private static async Task<DashboardOwedHoursResponse> OwedHoursAsync(
        AppDbContext database,
        Guid venueId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokDateAndHour(now).Date;

        var standing = await database.HourPackages
            .AsNoTracking()
            .Where(one => one.VenueId == venueId && one.ExpiredAt == null)
            .Select(one => new
            {
                one.PriceBaht,
                one.HoursSold,
                one.ExpiresOn,
                Left = one.Entries.Sum(entry => (int?)entry.Hours) ?? 0,
            })
            .ToListAsync(cancellationToken);

        var owed = standing.Where(one => one.Left > 0).ToList();

        return new DashboardOwedHoursResponse(
            owed.Sum(one => one.Left),
            owed.Sum(one => Packages.Worth(one.PriceBaht, one.HoursSold, one.Left)),
            owed.Count,
            owed.Count(one =>
                one.ExpiresOn >= today
                && one.ExpiresOn <= today.AddDays(Packages.RunningOutWithinDays)));
    }

    /// <summary>
    /// What the venue keeps from each booking played in the range, by the day it is played and
    /// the channel it came through (PRD 6.2). Only a booking whose money arrived keeps anything,
    /// and only once it has ended — played, not turned up for, or cancelled.
    /// </summary>
    private static async Task<Dictionary<DateOnly, KeptOnADay>> KeptByDayAsync(
        AppDbContext database,
        Guid venueId,
        DateTimeOffset since,
        DateTimeOffset until,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Found from the hours in the range, which the (CourtId, StartsAt) index answers, rather
        // than from every booking the venue has ever had. A booking whose first hour is before
        // the range but has a later one inside it is found too, and dropped below.
        var touching = database.BookingSlots
            .Where(slot =>
                slot.Court!.VenueId == venueId
                && slot.StartsAt >= since
                && slot.StartsAt < until)
            .Select(slot => slot.BookingId);

        var bookings = await database.Bookings
            .AsNoTracking()
            .Where(booking =>
                touching.Contains(booking.Id)
                && (booking.Status == BookingStatus.Confirmed
                    || booking.Status == BookingStatus.Completed
                    || booking.Status == BookingStatus.NoShow
                    || booking.Status == BookingStatus.Cancelled))
            .Select(booking => new
            {
                booking.Status,
                booking.Channel,
                booking.TotalBaht,
                booking.RefundDueBaht,
                booking.PaymentState,
                booking.DepositBaht,
                // Null where no package paid for it. Zero is a different answer: a package
                // booking that gave all its hours back kept nothing (PRD US-31).
                PaidWithHours = booking.PackageId == null ? (decimal?)null : booking.PackageBaht,
                // What arrived, which since deposits is not always the price (PRD US-28). A
                // booking the venue is holding nothing for is not revenue, whatever it cost.
                Taken = database.PaymentReceipts
                    .Where(receipt => receipt.BookingId == booking.Id)
                    .Sum(receipt => (decimal?)receipt.AmountBaht) ?? 0m,
                // What has actually gone back, which is not what is owed: US-18 lets a venue send
                // it in parts, and the two numbers are both worth reporting (PRD 7.3).
                Refunded = database.RefundRecords
                    .StillStanding()
                    .Where(record => record.BookingId == booking.Id)
                    .Sum(record => (decimal?)record.AmountBaht) ?? 0m,
                FirstStart = booking.Slots.Min(slot => slot.StartsAt),
                LastEnd = booking.Slots.Max(slot => slot.EndsAt),
            })
            .ToListAsync(cancellationToken);

        var byDay = new Dictionary<DateOnly, KeptOnADay>();

        foreach (var booking in bookings.Where(one => one.FirstStart >= since && one.FirstStart < until))
        {
            // Confirmed is only kept once it has been played: stored Confirmed reads as Completed
            // after its last hour (PRD 9.2), and before that it is advance money, not revenue.
            if (booking.Status == BookingStatus.Confirmed && booking.LastEnd > now)
            {
                continue;
            }

            var held = Math.Min(
                booking.TotalBaht,
                Takings.HeldFor(
                    booking.PaymentState,
                    booking.Taken,
                    booking.DepositBaht,
                    booking.PaidWithHours));

            // Money that never arrived is not revenue, and a booking held on a deposit is revenue
            // for the deposit until the desk collects the rest (PRD US-28). Asked of what the
            // venue holds rather than of what it keeps: a booking whose whole price is owed back
            // still happened, and the month has to count it and say what is owed on it.
            if (Takings.HeldUpToPrice(
                    booking.PaymentState,
                    booking.TotalBaht,
                    booking.Taken,
                    booking.DepositBaht) <= 0m)
            {
                continue;
            }

            // The same rule the platform's own numbers are built from (PRD 6.2).
            var keeps = Takings.KeptBy(
                booking.PaymentState,
                booking.TotalBaht,
                booking.RefundDueBaht,
                booking.Taken,
                booking.DepositBaht);
            var day = PlatformRequirements.BangkokDateAndHour(booking.FirstStart).Date;
            var so_far = byDay.GetValueOrDefault(day);

            byDay[day] = so_far with
            {
                Online = so_far.Online + (booking.Channel == BookingChannel.Staff ? 0 : keeps),
                Staff = so_far.Staff + (booking.Channel == BookingChannel.Staff ? keeps : 0),
                Bookings = so_far.Bookings + 1,
                RefundDue = so_far.RefundDue + booking.RefundDueBaht,
                Refunded = so_far.Refunded + booking.Refunded,
            };
        }

        return byDay;
    }

    /// <summary>What one day of play came to, as the report reads it (PRD 7.3).</summary>
    private readonly record struct KeptOnADay(
        decimal Online,
        decimal Staff,
        int Bookings,
        decimal RefundDue,
        decimal Refunded);

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
        // Lifted closures are read too: the hours before the lift were shut (US-15).
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
                taken: new HashSet<(Guid, int)>(),
                asItWas: true);

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
    /// <summary>
    /// What the venue lost and what it got back (PRD US-27). Two reads of the same hours: the
    /// ones given back, and the ones somebody has since taken — matched on the court and the
    /// hour, because that is what "the same hour" means to a venue.
    /// </summary>
    private static async Task<DashboardRecoveryResponse> RecoveryAsync(
        AppDbContext database,
        Guid venueId,
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        // Hours somebody had and gave back. A hold that ran out is not one of them: it was never
        // bought, and every abandoned pick would drown the number that matters.
        var lost = await database.BookingSlots
            .AsNoTracking()
            .Where(slot =>
                slot.Booking!.VenueId == venueId
                && slot.StartsAt >= since
                && slot.StartsAt < until
                && !slot.IsActive
                && LostStatuses.Contains(slot.Booking.Status))
            .Select(slot => new { slot.CourtId, slot.StartsAt })
            .Distinct()
            .ToListAsync(cancellationToken);

        if (lost.Count == 0)
        {
            return new DashboardRecoveryResponse(0, 0, 0m, 0, 0m);
        }

        // The hours that are taken again now, with what they went for and whether the queue is
        // what filled them.
        var refilled = await database.BookingSlots
            .AsNoTracking()
            .Where(slot =>
                slot.Booking!.VenueId == venueId
                && slot.StartsAt >= since
                && slot.StartsAt < until
                && slot.IsActive
                && RefilledStatuses.Contains(slot.Booking.Status))
            .Select(slot => new
            {
                slot.CourtId,
                slot.StartsAt,
                slot.BahtPerHour,
                FromQueue = database.WaitlistEntries.Any(
                    entry => entry.OfferedBookingId == slot.BookingId),
            })
            .ToListAsync(cancellationToken);

        var given = lost.Select(hour => (hour.CourtId, hour.StartsAt)).ToHashSet();
        var back = refilled
            .Where(hour => given.Contains((hour.CourtId, hour.StartsAt)))
            .ToList();

        return new DashboardRecoveryResponse(
            lost.Count,
            back.Count,
            back.Sum(hour => hour.BahtPerHour),
            back.Count(hour => hour.FromQueue),
            back.Where(hour => hour.FromQueue).Sum(hour => hour.BahtPerHour));
    }

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
            // Most bookings owe nothing, and asking that first spares them the sum.
            booking => booking.RefundDueBaht > 0
                && booking.RefundDueBaht > database.RefundRecords
                .Where(record => record.BookingId == booking.Id && record.VoidedAt == null)
                .Sum(record => record.AmountBaht),
            cancellationToken);

        return new DashboardAttentionResponse(slips, unanswered, outstanding);
    }
}
