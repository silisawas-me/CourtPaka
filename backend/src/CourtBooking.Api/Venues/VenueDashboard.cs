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
    decimal RefundedBaht,
    /// <summary>
    /// The counter's shop that day: rung up less handed back, each on the day the money moved —
    /// the same rule as <see cref="DashboardTradeResponse.ShopBaht"/>, split by day (owner app).
    /// </summary>
    decimal ShopBaht = 0m);

/// <summary>Money that came in over the range by the form it came in (owner app, PRD US-26).</summary>
public sealed record DashboardMethodResponse(string Method, decimal Baht);

/// <summary>A thing the counter sold over the range: how many, and for how much (US-32).</summary>
public sealed record DashboardItemResponse(Guid ItemId, string Name, int Quantity, decimal Baht);

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
    DashboardTradeResponse Trade,
    /// <summary>
    /// Every receipt counted in the range, by how it was paid — court, package and shop money
    /// alike, because that is what went into the till or the account (owner app PR-5).
    /// </summary>
    DashboardMethodResponse[] ByMethod,
    /// <summary>The shop's best sellers over the range, most money first (owner app PR-5).</summary>
    DashboardItemResponse[] TopItems,
    /// <summary>
    /// Court money plus shop money over the same number of days just before the range, so the
    /// page can say how this period compares — by the same rules as the range itself.
    /// </summary>
    decimal PriorBaht);

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
        // Days are the venue's own: a venue open until 02:00 counts Friday's last hours on Friday
        // (thai-fit T4).
        var dayStartsHour = await VenueDay.DayStartsHourAsync(database, venueId, cancellationToken);
        var since = VenueClock.Window(first, dayStartsHour).From;
        var until = VenueClock.Window(last, dayStartsHour).Until;

        var kept = await KeptByDayAsync(
            database, venueId, since, until, dayStartsHour, now, cancellationToken);
        var hours = await HoursByDayAsync(
            database, venueId, first, last, since, until, dayStartsHour, cancellationToken);
        var shopByDay = await ShopByDayAsync(
            database, venueId, since, until, dayStartsHour, cancellationToken);

        var days = Enumerable.Range(0, last.DayNumber - first.DayNumber + 1)
            .Select(offset => first.AddDays(offset))
            .Select(date =>
            {
                var money = kept.GetValueOrDefault(date);
                return new DashboardDayResponse(
                    date,
                    money.Online,
                    money.Staff,
                    hours.GetValueOrDefault(date)?.Sum(hour => hour.Sellable) ?? 0,
                    hours.GetValueOrDefault(date)?.Sum(hour => hour.Booked) ?? 0,
                    money.Bookings,
                    money.RefundDue,
                    money.Refunded,
                    shopByDay.GetValueOrDefault(date));
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
                cancellationToken),
            await ByMethodAsync(database, venueId, first, last, cancellationToken),
            await TopItemsAsync(database, venueId, since, until, cancellationToken),
            await PriorAsync(database, venueId, first, last, now, cancellationToken));
    }

    /// <summary>How many of the shop's best sellers the page is given.</summary>
    public const int TopItemsShown = 5;

    /// <summary>
    /// The shop's money per venue day, by the rule the range total uses: a sale on the day it was
    /// rung up, a hand-back on the day it was handed back (PRD US-32).
    /// </summary>
    private static async Task<Dictionary<DateOnly, decimal>> ShopByDayAsync(
        AppDbContext database,
        Guid venueId,
        DateTimeOffset since,
        DateTimeOffset until,
        int dayStartsHour,
        CancellationToken cancellationToken)
    {
        var sales = await database.ShopSales
            .AsNoTracking()
            .Where(sale =>
                sale.VenueId == venueId
                && ((sale.SoldAt >= since && sale.SoldAt < until)
                    || (sale.CancelledAt >= since && sale.CancelledAt < until)))
            .Select(sale => new { sale.SoldAt, sale.CancelledAt, sale.TotalBaht })
            .ToListAsync(cancellationToken);

        var byDay = new Dictionary<DateOnly, decimal>();
        foreach (var sale in sales)
        {
            if (sale.SoldAt >= since && sale.SoldAt < until)
            {
                var day = VenueClock.DayAndHour(sale.SoldAt, dayStartsHour).Date;
                byDay[day] = byDay.GetValueOrDefault(day) + sale.TotalBaht;
            }

            if (sale.CancelledAt is { } back && back >= since && back < until)
            {
                var day = VenueClock.DayAndHour(back, dayStartsHour).Date;
                byDay[day] = byDay.GetValueOrDefault(day) - sale.TotalBaht;
            }
        }

        return byDay;
    }

    /// <summary>
    /// Receipts counted on the days in the range, by method. Counted by the till's day
    /// (<see cref="PaymentReceipt.CountsOn"/>), which is the day the money page counts them on.
    /// </summary>
    private static async Task<DashboardMethodResponse[]> ByMethodAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly first,
        DateOnly last,
        CancellationToken cancellationToken)
    {
        var sums = await database.PaymentReceipts
            .AsNoTracking()
            .Where(receipt =>
                receipt.VenueId == venueId && receipt.CountsOn >= first && receipt.CountsOn <= last)
            .GroupBy(receipt => receipt.Method)
            .Select(group => new { Method = group.Key, Baht = group.Sum(receipt => receipt.AmountBaht) })
            .ToListAsync(cancellationToken);

        return
        [
            .. sums
                .OrderByDescending(one => one.Baht)
                .Select(one => new DashboardMethodResponse(one.Method.ToString(), one.Baht)),
        ];
    }

    /// <summary>What the counter sold most of, in money, among the sales still standing.</summary>
    private static async Task<DashboardItemResponse[]> TopItemsAsync(
        AppDbContext database,
        Guid venueId,
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        var lines = await database.ShopSaleLines
            .AsNoTracking()
            .Where(line =>
                line.Sale!.VenueId == venueId
                && line.Sale.CancelledAt == null
                && line.Sale.SoldAt >= since
                && line.Sale.SoldAt < until)
            .Select(line => new { line.ItemId, line.Name, line.Quantity, line.EachBaht, line.Sale!.SoldAt })
            .ToListAsync(cancellationToken);

        return
        [
            .. lines
                .GroupBy(line => line.ItemId)
                .Select(item => new DashboardItemResponse(
                    item.Key,
                    // The name it was last sold under, which is the one the counter knows it by.
                    item.OrderByDescending(line => line.SoldAt).First().Name,
                    item.Sum(line => line.Quantity),
                    item.Sum(line => line.EachBaht * line.Quantity)))
                .OrderByDescending(item => item.Baht)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .Take(TopItemsShown),
        ];
    }

    /// <summary>Court money kept plus shop money, over the days just before the range.</summary>
    private static async Task<decimal> PriorAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly first,
        DateOnly last,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var length = last.DayNumber - first.DayNumber + 1;
        var priorFirst = first.AddDays(-length);
        var dayStartsHour = await VenueDay.DayStartsHourAsync(database, venueId, cancellationToken);
        var since = VenueClock.Window(priorFirst, dayStartsHour).From;
        var until = VenueClock.Window(first, dayStartsHour).From;

        var kept = await KeptByDayAsync(
            database, venueId, since, until, dayStartsHour, now, cancellationToken);
        var shop = await ShopByDayAsync(
            database, venueId, since, until, dayStartsHour, cancellationToken);

        return kept.Values.Sum(day => day.Online + day.Staff) + shop.Values.Sum();
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
        int dayStartsHour,
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

            // Money that never arrived is not revenue, and a booking held on a deposit is revenue
            // for the deposit until the desk collects the rest (PRD US-28). Asked of what the
            // venue holds rather than of what it keeps: a booking whose whole price is owed back
            // still happened, and the month has to count it and say what is owed on it.
            if (Takings.HeldUpToPrice(
                    booking.PaymentState,
                    booking.TotalBaht,
                    booking.Taken,
                    booking.DepositBaht,
                    booking.PaidWithHours) <= 0m)
            {
                continue;
            }

            // The same rule the platform's own numbers are built from (PRD 6.2).
            var keeps = Takings.KeptBy(
                booking.PaymentState,
                booking.TotalBaht,
                booking.RefundDueBaht,
                booking.Taken,
                booking.DepositBaht,
                booking.PaidWithHours);
            var day = VenueClock.DayAndHour(booking.FirstStart, dayStartsHour).Date;
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
    private static async Task<Dictionary<DateOnly, HourUse[]>> HoursByDayAsync(
            AppDbContext database,
            Guid venueId,
            DateOnly first,
            DateOnly last,
            DateTimeOffset since,
            DateTimeOffset until,
            int dayStartsHour,
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
            .Select(slot => (slot.CourtId, At: VenueClock.DayAndHour(slot.StartsAt, dayStartsHour)))
            .ToLookup(slot => slot.At.Date, slot => (slot.CourtId, slot.At.Hour));

        var byDay = new Dictionary<DateOnly, HourUse[]>();

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
                asItWas: true,
                dayStartsHour: dayStartsHour);

            var usedThatDay = usedByDay[date].Distinct().ToArray();
            byDay[date] =
            [
                .. day.Hours.Select(hour => new HourUse(
                    hour,
                    day.Courts.Count(court => day.IsSellable(court.Id, hour)),
                    usedThatDay.Count(slot => slot.Hour == hour && day.IsSellable(slot.CourtId, hour)))),
            ];
        }

        return byDay;
    }

    /// <summary>
    /// One hour of one day: how many court-hours were for sale in it and how many of those were
    /// used. The day's figures are these summed, so a day and its hours can never disagree.
    /// </summary>
    public readonly record struct HourUse(int Hour, int Sellable, int Booked);

    /// <summary>
    /// Today at one venue, as the owner's overview of all their venues reads it (badPaka 2c):
    /// what was kept, how many bookings that came from, and the hours — by the same rules as the
    /// venue's own dashboard, from the same code, so the two pages cannot show two numbers.
    /// </summary>
    public static async Task<(decimal KeptBaht, int Bookings, HourUse[] Hours)> TodayAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly today,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var dayStartsHour = await VenueDay.DayStartsHourAsync(database, venueId, cancellationToken);
        var (since, until) = VenueClock.Window(today, dayStartsHour);

        var kept = (await KeptByDayAsync(
                database, venueId, since, until, dayStartsHour, now, cancellationToken))
            .GetValueOrDefault(today);
        var hours = await HoursByDayAsync(
            database, venueId, today, today, since, until, dayStartsHour, cancellationToken);

        return (kept.Online + kept.Staff, kept.Bookings, hours.GetValueOrDefault(today) ?? []);
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
