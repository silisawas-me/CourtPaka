using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

public sealed record TakePaymentRequest(decimal AmountBaht, string Method, string? Note);

/// <summary>One amount the venue took, as the counter reads it back (PRD US-26).</summary>
public sealed record PaymentReceiptResponse(
    Guid Id,
    /// <summary>The booking it was for, or null where it was a package being sold (US-31).</summary>
    Guid? BookingId,
    /// <summary>The package that was sold, where that is what it was.</summary>
    Guid? PackageId,
    decimal AmountBaht,
    string Method,
    DateTimeOffset ReceivedAt,
    string? Note);

/// <summary>What one booking has been paid, and what that leaves (PRD US-26).</summary>
public sealed record TakingsResponse(
    decimal TotalBaht,
    decimal TakenBaht,
    decimal OutstandingBaht,
    PaymentReceiptResponse[] Receipts);

public sealed record CloseDayRequest(decimal OpeningFloatBaht, decimal CountedCashBaht, string? Note);

/// <summary>
/// A day's money, whether or not it has been counted yet (PRD US-26). The screen draws the count
/// from this and the server settles it with the same numbers.
/// </summary>
public sealed record DayMoneyResponse(
    DateOnly Date,
    decimal TakenBaht,
    decimal CashBaht,
    decimal PromptPayBaht,
    decimal CardBaht,
    /// <summary>Cash the venue handed back that day (PRD US-18), which leaves the till too.</summary>
    decimal CashRefundedBaht,
    decimal OutstandingBaht,
    PaymentReceiptResponse[] CashReceipts,
    DailyClosingResponse? Closed,
    /// <summary>
    /// Where the difference might have come from, once the day has been counted and did not come
    /// out even. Empty until then, and empty when nothing matches (PRD US-26).
    /// </summary>
    MoneyLeadResponse[] Leads);

/// <summary>
/// One row whose amount is exactly what the till came out by (PRD US-26). The system does not say
/// what happened; it says where to look, and the person who was there decides.
/// </summary>
public sealed record MoneyLeadResponse(
    string Kind,
    decimal AmountBaht,
    Guid? BookingId,
    DateTimeOffset? At,
    string? Note);

public sealed record DailyClosingResponse(
    DateOnly Date,
    decimal OpeningFloatBaht,
    decimal ExpectedCashBaht,
    decimal CountedCashBaht,
    decimal DifferenceBaht,
    string? Note,
    DateTimeOffset ClosedAt);

public static class MoneyErrorCodes
{
    /// <summary>More than the booking still owes, or nothing at all.</summary>
    public const string InvalidAmount = "money.invalid_amount";

    public const string UnknownMethod = "money.unknown_method";

    /// <summary>A day is counted once (PRD US-26).</summary>
    public const string AlreadyClosed = "money.already_closed";

    /// <summary>A day that has not finished cannot be counted.</summary>
    public const string DayNotOver = "money.day_not_over";

    public const string InvalidFloat = "money.invalid_float";

    /// <summary>Nothing is owed on it, or it is not a booking the venue is going to honour.</summary>
    public const string NotTakeable = "money.not_takeable";
}

/// <summary>
/// Money as it crosses the counter (PRD US-26): taken in parts, in the form it arrived, and
/// counted at the end of the day.
///
/// It sits beside the slip queue rather than replacing it. A booker who transfers and sends a
/// slip is still verified there; this is for the notes and the card machine at the desk, and for
/// the deposit now, rest later that a phone booking turns into.
/// </summary>
public static class CounterMoneyEndpoints
{
    public static void MapCounterMoneyEndpoints(this RouteGroupBuilder bookings)
    {
        bookings.MapGet("/{bookingId:guid}/payments", TakingsAsync);
        bookings.MapPost("/{bookingId:guid}/payments", TakeAsync);
    }

    /// <summary>The day's money and its count, under the venue rather than under a booking.</summary>
    public static void MapDayMoneyEndpoints(this RouteGroupBuilder venue)
    {
        // Reading what a day took is a report; closing it is the same permission, because whoever
        // counts the till is whoever is trusted with the numbers (PRD US-26).
        venue.MapGet("/money", DayAsync)
            .RequireAuthorization(VenuePolicies.NeedsEvenWhenSuspended(VenuePermissions.ViewReports));
        venue.MapPost("/money/closing", CloseAsync)
            .RequireAuthorization(VenuePolicies.NeedsEvenWhenSuspended(VenuePermissions.ViewReports));
    }

    private static async Task<Results<Ok<TakingsResponse>, ProblemHttpResult>> TakingsAsync(
        Guid venueId,
        Guid bookingId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var booking = await database.Bookings
            .AsNoTracking()
            .Where(one => one.Id == bookingId && one.VenueId == venueId)
            .Select(one => new { one.TotalBaht })
            .SingleOrDefaultAsync(cancellationToken);

        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        return TypedResults.Ok(await ReadTakingsAsync(database, bookingId, booking.TotalBaht, cancellationToken));
    }

    /// <summary>
    /// Writes down an amount the venue has just taken. It refuses more than is owed — a till that
    /// says it took 1,200 for a 900 booking is a till nobody can count — and once the whole
    /// amount is in, the booking's payment state says so, which is what everything else reads.
    ///
    /// It answers with the booking as the day list draws it, like every other door the counter
    /// presses: what has been taken, what that leaves, and which doors are open now.
    /// </summary>
    private static async Task<Results<Ok<VenueBookingResponse>, ProblemHttpResult>> TakeAsync(
        Guid venueId,
        Guid bookingId,
        TakePaymentRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<PaymentMethod>(request.Method, out var method)
            || !Enum.IsDefined(method))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, MoneyErrorCodes.UnknownMethod);
        }

        var amount = decimal.Round(request.AmountBaht, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, MoneyErrorCodes.InvalidAmount);
        }

        var note = request.Note?.Trim();
        if (note is { Length: > PaymentReceipt.NoteMaxLength })
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, MoneyErrorCodes.InvalidAmount);
        }

        var now = timeProvider.GetUtcNow();
        var membership = venue.Require();

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        await QueueForTheMoneyAsync(database, bookingId, cancellationToken);

        // With its hours, because what a booking reads as depends on them: a hold whose fifteen
        // minutes are up is Expired and owes nothing forwards (PRD 9.2).
        var booking = await database.Bookings
            .AsNoTracking()
            .Include(one => one.Slots)
            .Where(one => one.Id == bookingId && one.VenueId == venueId)
            .SingleOrDefaultAsync(cancellationToken);

        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        var status = BookedSlots.StatusAt(booking, now);
        var taken = await TakenAsync(database, bookingId, cancellationToken);
        var outstanding = Takings.OutstandingOf(booking.TotalBaht, taken);
        if (!Takings.CanTake(status, booking.PaymentState, outstanding))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, MoneyErrorCodes.NotTakeable);
        }

        if (amount > outstanding)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, MoneyErrorCodes.InvalidAmount);
        }

        database.PaymentReceipts.Add(new PaymentReceipt
        {
            BookingId = bookingId,
            VenueId = venueId,
            AmountBaht = amount,
            Method = method,
            ReceivedAt = now,
            ReceivedByUserId = membership.UserId,
            Note = string.IsNullOrEmpty(note) ? null : note,
        });

        // Paid in full is what the rest of the system reads as "the venue has the money" (6.2),
        // and for a booking that was still waiting it is also the answer it was waiting for: the
        // hours are paid for, so they are confirmed rather than left to run out (PRD BR-02).
        var settled = amount == outstanding;
        var confirms = settled && Takings.PayingInFullConfirms(status);

        // Hours that are already behind it land on Completed the moment they are confirmed, and
        // both steps are recorded — a history that skips the one the clock made has a hole in it
        // (PRD 9.2, and US-12 lands a late-answered slip the same way).
        var landed = BookingTransitions.LandingFor(
            BookingStatus.Confirmed, booking.Slots.Max(slot => slot.EndsAt), now);

        // What the history will say about this payment. Written from the same answers the update
        // is made with, so the rows and the event lines below cannot tell different stories.
        var recorded = new List<BookingStatusChange>();

        if (settled && booking.PaymentState != PaymentState.Received)
        {
            // Conditional on the status that was read, so a slip somebody else answered a moment
            // ago is not answered again on top of the answer it already has (PRD BR-04). Writing
            // the status back unchanged is what the WHERE already pins it to.
            var moved = await database.Bookings
                .Where(one => one.Id == bookingId && one.Status == booking.Status)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(one => one.PaymentState, PaymentState.Received)
                        .SetProperty(one => one.Status, confirms ? landed : booking.Status),
                    cancellationToken);

            if (moved == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ApiProblem.Of(StatusCodes.Status409Conflict, MoneyErrorCodes.NotTakeable);
            }

            if (confirms)
            {
                recorded.AddRange(BookingTransitions.RecordsFor(
                    bookingId,
                    booking.Status,
                    BookingStatus.Confirmed,
                    landed,
                    membership.UserId,
                    now));
            }
            else
            {
                // The booking did not move, but the venue now says it has the money, and who
                // decided that is the first thing a complaint asks (PRD 6.1, US-13 does the same).
                recorded.Add(BookingTransitions.Settled(
                    bookingId, booking.Status, PaymentState.Received, membership.UserId, now));
            }

            database.BookingStatusChanges.AddRange(recorded);
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var events = AppEvents.For(loggers);
        events.LogInformation(
            "payment_taken {BookingId} {VenueId} {Baht} {Method}", bookingId, venueId, amount, method);

        foreach (var change in recorded)
        {
            // A row that stays where it was is the venue answering for the money, not a move.
            if (change.From == change.To)
            {
                events.LogInformation(
                    BookingTransitions.SettledTemplate,
                    bookingId,
                    venueId,
                    PaymentState.Received,
                    booking.RefundDueBaht);
            }
            else
            {
                events.LogInformation(
                    BookingTransitions.EventTemplate(change.To),
                    bookingId,
                    venueId,
                    booking.RefundDueBaht);
            }
        }

        // The row as the counter reads it, like every other door on the day list: the money it
        // has taken, what that leaves, and which doors are open now (PRD US-13).
        return TypedResults.Ok(await VenueBookingEndpoints.OneDrawnAsync(
            database, venueId, bookingId, venue, now, cancellationToken));
    }

    /// <summary>
    /// What a day took, by the form it came in, and what the till should therefore hold. Every
    /// number is counted from the receipts rather than kept as a running total, so a receipt
    /// written late lands in the day it belongs to.
    /// </summary>
    private static async Task<Ok<DayMoneyResponse>> DayAsync(
        Guid venueId,
        DateOnly? date,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var day = date ?? PlatformRequirements.BangkokToday(timeProvider);
        var (from, until) = await TillDayAsync(database, venueId, day, cancellationToken);

        var receipts = await database.PaymentReceipts
            .AsNoTracking()
            .Where(receipt =>
                receipt.VenueId == venueId
                && receipt.ReceivedAt >= from
                && receipt.ReceivedAt < until)
            .OrderBy(receipt => receipt.ReceivedAt)
            .Select(receipt => new
            {
                receipt.Id,
                receipt.BookingId,
                receipt.PackageId,
                receipt.AmountBaht,
                receipt.Method,
                receipt.ReceivedAt,
                receipt.Note,
            })
            .ToListAsync(cancellationToken);

        // Cash the venue sent back that day leaves the same till the cash came into (US-18).
        var cashBack = await database.RefundRecords
            .StillStanding()
            .Where(record =>
                record.Booking!.VenueId == venueId
                && record.Method == RefundMethod.Cash
                && record.RecordedAt >= from
                && record.RecordedAt < until)
            .Select(record => new CashHandedBack(
                record.BookingId, record.AmountBaht, record.RecordedAt))
            .ToListAsync(cancellationToken);

        var cashRefunded = cashBack.Sum(record => record.AmountBaht);

        // What the day's bookings are still short, counted the same way the rows are.
        var owing = await OwingOnAsync(database, venueId, day, cancellationToken);
        var outstanding = owing.Sum(one => one.Baht);

        var closed = await database.DailyClosings
            .AsNoTracking()
            .Where(closing => closing.VenueId == venueId && closing.Date == day)
            .Select(closing => new DailyClosingResponse(
                closing.Date,
                closing.OpeningFloatBaht,
                closing.ExpectedCashBaht,
                closing.CountedCashBaht,
                closing.DifferenceBaht,
                closing.Note,
                closing.ClosedAt))
            .SingleOrDefaultAsync(cancellationToken);

        decimal By(PaymentMethod method) =>
            receipts.Where(receipt => receipt.Method == method).Sum(receipt => receipt.AmountBaht);

        PaymentReceiptResponse[] cashTaken =
        [
            .. receipts
                .Where(receipt => receipt.Method == PaymentMethod.Cash)
                .Select(receipt => new PaymentReceiptResponse(
                    receipt.Id,
                    receipt.BookingId,
                    receipt.PackageId,
                    receipt.AmountBaht,
                    receipt.Method.ToString(),
                    receipt.ReceivedAt,
                    receipt.Note)),
        ];

        return TypedResults.Ok(new DayMoneyResponse(
            day,
            receipts.Sum(receipt => receipt.AmountBaht),
            By(PaymentMethod.Cash),
            By(PaymentMethod.PromptPay),
            By(PaymentMethod.Card),
            cashRefunded,
            outstanding,
            cashTaken,
            closed,
            LeadsFor(closed, cashTaken, cashBack, owing)));
    }

    /// <summary>
    /// The rows whose amount is exactly what the count came out by, for a day that has been
    /// counted and did not balance (PRD US-26). Cash written down as taken and cash written down
    /// as handed back are the two that move the till; a booking still owing is the third, because
    /// money taken and not written down is the usual reason a till is over.
    ///
    /// It offers; it does not conclude. Nothing here changes a number, and a day that balanced
    /// gets an empty list rather than a list of coincidences.
    /// </summary>
    private static MoneyLeadResponse[] LeadsFor(
        DailyClosingResponse? closed,
        IReadOnlyList<PaymentReceiptResponse> cashTaken,
        IReadOnlyList<CashHandedBack> cashBack,
        IReadOnlyList<StillOwed> owing)
    {
        if (closed is null)
        {
            return [];
        }

        var difference = closed.DifferenceBaht;

        return
        [
            .. cashTaken
                .Where(receipt => Takings.Explains(receipt.AmountBaht, difference))
                .Select(receipt => new MoneyLeadResponse(
                    nameof(MoneyLeadKind.CashTaken),
                    receipt.AmountBaht,
                    receipt.BookingId,
                    receipt.ReceivedAt,
                    receipt.Note)),

            .. cashBack
                .Where(record => Takings.Explains(record.AmountBaht, difference))
                .Select(record => new MoneyLeadResponse(
                    nameof(MoneyLeadKind.CashHandedBack),
                    record.AmountBaht,
                    record.BookingId,
                    record.At,
                    null)),

            .. owing
                .Where(one => Takings.Explains(one.Baht, difference))
                .Select(one => new MoneyLeadResponse(
                    nameof(MoneyLeadKind.StillOwed),
                    one.Baht,
                    one.BookingId,
                    null,
                    null)),
        ];
    }

    /// <summary>Cash that left the till that day (PRD US-18).</summary>
    private sealed record CashHandedBack(Guid BookingId, decimal AmountBaht, DateTimeOffset At);

    /// <summary>A booking of that day and what it is still short.</summary>
    private sealed record StillOwed(Guid BookingId, decimal Baht);

    /// <summary>
    /// Counts the day and writes it down (PRD US-26). What the till should hold is the server's
    /// arithmetic, not the screen's: the person closing types what they counted, and the
    /// difference is worked out here from the receipts.
    /// </summary>
    private static async Task<Results<Ok<DailyClosingResponse>, ProblemHttpResult>> CloseAsync(
        Guid venueId,
        DateOnly? date,
        CloseDayRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);
        var day = date ?? today;

        if (day > today)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, MoneyErrorCodes.DayNotOver);
        }

        if (request.OpeningFloatBaht < 0 || request.CountedCashBaht < 0)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, MoneyErrorCodes.InvalidFloat);
        }

        var note = request.Note?.Trim();
        if (note is { Length: > DailyClosing.NoteMaxLength })
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, MoneyErrorCodes.InvalidFloat);
        }

        var (from, until) = await TillDayAsync(database, venueId, day, cancellationToken);

        var cashIn = await database.PaymentReceipts
            .Where(receipt =>
                receipt.VenueId == venueId
                && receipt.Method == PaymentMethod.Cash
                && receipt.ReceivedAt >= from
                && receipt.ReceivedAt < until)
            .SumAsync(receipt => (decimal?)receipt.AmountBaht, cancellationToken) ?? 0m;

        var cashOut = await database.RefundRecords
            .StillStanding()
            .Where(record =>
                record.Booking!.VenueId == venueId
                && record.Method == RefundMethod.Cash
                && record.RecordedAt >= from
                && record.RecordedAt < until)
            .SumAsync(record => (decimal?)record.AmountBaht, cancellationToken) ?? 0m;

        var expected = Takings.ExpectedCash(request.OpeningFloatBaht, cashIn, cashOut);
        var counted = decimal.Round(request.CountedCashBaht, 2, MidpointRounding.AwayFromZero);

        var closing = new DailyClosing
        {
            VenueId = venueId,
            Date = day,
            OpeningFloatBaht = decimal.Round(request.OpeningFloatBaht, 2, MidpointRounding.AwayFromZero),
            ExpectedCashBaht = expected,
            CountedCashBaht = counted,
            DifferenceBaht = decimal.Round(counted - expected, 2, MidpointRounding.AwayFromZero),
            Note = string.IsNullOrEmpty(note) ? null : note,
            ClosedByUserId = venue.Require().UserId,
            ClosedAt = timeProvider.GetUtcNow(),
        };

        database.DailyClosings.Add(closing);

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DbErrors.IsUniqueViolation(exception))
        {
            // Two people counted the same till at the same time. The first count is the count.
            return ApiProblem.Of(StatusCodes.Status409Conflict, MoneyErrorCodes.AlreadyClosed);
        }

        AppEvents.For(loggers).LogInformation(
            "day_closed {VenueId} {Date} {Difference}", venueId, day, closing.DifferenceBaht);

        return TypedResults.Ok(new DailyClosingResponse(
            closing.Date,
            closing.OpeningFloatBaht,
            closing.ExpectedCashBaht,
            closing.CountedCashBaht,
            closing.DifferenceBaht,
            closing.Note,
            closing.ClosedAt));
    }

    /// <summary>
    /// When this venue's day starts and stops taking money (PRD US-26), read from the counts
    /// either side of it. Asked here rather than worked out by each reader, because the page that
    /// shows the day and the count that closes it have to be looking at the same money.
    /// </summary>
    private static async Task<(DateTimeOffset From, DateTimeOffset Until)> TillDayAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly day,
        CancellationToken cancellationToken)
    {
        var counts = await database.DailyClosings
            .AsNoTracking()
            .Where(closing =>
                closing.VenueId == venueId
                && (closing.Date == day || closing.Date == day.AddDays(-1)))
            .Select(closing => new { closing.Date, closing.ClosedAt })
            .ToListAsync(cancellationToken);

        return Takings.TillDay(
            day,
            counts.SingleOrDefault(one => one.Date == day.AddDays(-1))?.ClosedAt,
            counts.SingleOrDefault(one => one.Date == day)?.ClosedAt);
    }

    /// <summary>What has been taken for one booking so far.</summary>
    /// <summary>
    /// Queues this request behind everybody else who is about to read what a booking has had paid
    /// against it and then write from that reading (PRD US-26, US-18). Two reads of "900 owed"
    /// that both act on it is money the venue cannot account for, and the reading has to happen
    /// inside the transaction that acts on it or the lock buys nothing.
    ///
    /// The advisory lock holds off the other tills; the share lock on the row holds this behind
    /// anything taking the booking itself — a slip being answered, a cancellation — because those
    /// take the row exclusively and this would otherwise read a state they are about to change.
    /// </summary>
    internal static async Task QueueForTheMoneyAsync(
        AppDbContext database,
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({bookingId.ToString()}, 0))",
            cancellationToken);

        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Bookings\" WHERE \"Id\" = {bookingId} FOR SHARE",
            cancellationToken);
    }

    public static async Task<decimal> TakenAsync(
        AppDbContext database,
        Guid bookingId,
        CancellationToken cancellationToken) =>
        await database.PaymentReceipts
            .Where(receipt => receipt.BookingId == bookingId)
            .SumAsync(receipt => (decimal?)receipt.AmountBaht, cancellationToken) ?? 0m;

    private static async Task<TakingsResponse> ReadTakingsAsync(
        AppDbContext database,
        Guid bookingId,
        decimal totalBaht,
        CancellationToken cancellationToken)
    {
        var receipts = await database.PaymentReceipts
            .AsNoTracking()
            .Where(receipt => receipt.BookingId == bookingId)
            .OrderBy(receipt => receipt.ReceivedAt)
            .ThenBy(receipt => receipt.Id)
            .Select(receipt => new PaymentReceiptResponse(
                receipt.Id,
                receipt.BookingId,
                receipt.PackageId,
                receipt.AmountBaht,
                receipt.Method.ToString(),
                receipt.ReceivedAt,
                receipt.Note))
            .ToArrayAsync(cancellationToken);

        var taken = receipts.Sum(receipt => receipt.AmountBaht);
        return new TakingsResponse(
            totalBaht, taken, Takings.OutstandingOf(totalBaht, taken), receipts);
    }

    /// <summary>
    /// What each of the day's bookings is still short, and therefore what the day is. Only the
    /// ones that are still going to be played or were: a booking nobody took up owes nothing.
    ///
    /// Per booking rather than as one sum, because a booking that owes exactly what the till is
    /// out by is the first place to look when a count does not balance (PRD US-26).
    /// </summary>
    private static async Task<IReadOnlyList<StillOwed>> OwingOnAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly day,
        CancellationToken cancellationToken)
    {
        var from = PlatformRequirements.BangkokHour(day, 0);
        var until = PlatformRequirements.BangkokHour(day.AddDays(1), 0);

        var owed = await database.Bookings
            .AsNoTracking()
            .Where(booking =>
                booking.VenueId == venueId
                && booking.Slots.Any(slot => slot.StartsAt >= from && slot.StartsAt < until)
                // The same bookings the counter is offered a door on, and on the same terms
                // (Takings.CanTake): one the venue already says it has the money for owes
                // nothing, whatever its receipts add up to. A booking paid for with hours has no
                // receipts at all and would otherwise be offered as the explanation for a till
                // that came out over — sending somebody to look for cash nobody ever owed.
                && Takings.StillOwing.Contains(booking.Status)
                && booking.PaymentState != PaymentState.Received)
            .Select(booking => new
            {
                booking.Id,
                booking.TotalBaht,
                Taken = database.PaymentReceipts
                    .Where(receipt => receipt.BookingId == booking.Id)
                    .Sum(receipt => (decimal?)receipt.AmountBaht) ?? 0m,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. owed
                .Select(one => new StillOwed(
                    one.Id, Takings.OutstandingOf(one.TotalBaht, one.Taken)))
                .Where(one => one.Baht > 0),
        ];
    }
}
