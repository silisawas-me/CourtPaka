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
    Guid BookingId,
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
    DailyClosingResponse? Closed);

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
    /// </summary>
    private static async Task<Results<Ok<TakingsResponse>, ProblemHttpResult>> TakeAsync(
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

        // The same lock every writer of this booking's money takes, so two people at two tills
        // cannot both read "900 owed" and both take it (US-18 does this for refunds).
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({bookingId.ToString()}, 0))",
            cancellationToken);

        var booking = await database.Bookings
            .Where(one => one.Id == bookingId && one.VenueId == venueId)
            .Select(one => new { one.TotalBaht, one.Status, one.PaymentState })
            .SingleOrDefaultAsync(cancellationToken);

        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        var taken = await TakenAsync(database, bookingId, cancellationToken);
        var outstanding = Takings.OutstandingOf(booking.TotalBaht, taken);
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

        // Paid in full is what the rest of the system reads as "the venue has the money" (6.2).
        if (amount == outstanding && booking.PaymentState != PaymentState.Received)
        {
            await database.Bookings
                .Where(one => one.Id == bookingId)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(one => one.PaymentState, PaymentState.Received),
                    cancellationToken);
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "payment_taken {BookingId} {VenueId} {Baht} {Method}", bookingId, venueId, amount, method);

        return TypedResults.Ok(
            await ReadTakingsAsync(database, bookingId, booking.TotalBaht, cancellationToken));
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
        var from = PlatformRequirements.BangkokHour(day, 0);
        var until = PlatformRequirements.BangkokHour(day.AddDays(1), 0);

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
                receipt.AmountBaht,
                receipt.Method,
                receipt.ReceivedAt,
                receipt.Note,
            })
            .ToListAsync(cancellationToken);

        // Cash the venue sent back that day leaves the same till the cash came into (US-18).
        var cashRefunded = await database.RefundRecords
            .StillStanding()
            .Where(record =>
                record.Booking!.VenueId == venueId
                && record.Method == RefundMethod.Cash
                && record.RecordedAt >= from
                && record.RecordedAt < until)
            .SumAsync(record => (decimal?)record.AmountBaht, cancellationToken) ?? 0m;

        // What the day's bookings are still short, counted the same way the rows are.
        var outstanding = await OutstandingOnAsync(database, venueId, day, cancellationToken);

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

        return TypedResults.Ok(new DayMoneyResponse(
            day,
            receipts.Sum(receipt => receipt.AmountBaht),
            By(PaymentMethod.Cash),
            By(PaymentMethod.PromptPay),
            By(PaymentMethod.Card),
            cashRefunded,
            outstanding,
            [
                .. receipts
                    .Where(receipt => receipt.Method == PaymentMethod.Cash)
                    .Select(receipt => new PaymentReceiptResponse(
                        receipt.Id,
                        receipt.BookingId,
                        receipt.AmountBaht,
                        receipt.Method.ToString(),
                        receipt.ReceivedAt,
                        receipt.Note)),
            ],
            closed));
    }

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

        var from = PlatformRequirements.BangkokHour(day, 0);
        var until = PlatformRequirements.BangkokHour(day.AddDays(1), 0);

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

    /// <summary>What has been taken for one booking so far.</summary>
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
    /// What the day's bookings are still short. Only the ones that are still going to be played
    /// or were: a booking nobody took up owes nothing.
    /// </summary>
    private static async Task<decimal> OutstandingOnAsync(
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
                && (booking.Status == BookingStatus.Confirmed
                    || booking.Status == BookingStatus.Completed
                    || booking.Status == BookingStatus.NoShow
                    || booking.Status == BookingStatus.PendingVerification))
            .Select(booking => new
            {
                booking.TotalBaht,
                Taken = database.PaymentReceipts
                    .Where(receipt => receipt.BookingId == booking.Id)
                    .Sum(receipt => (decimal?)receipt.AmountBaht) ?? 0m,
            })
            .ToListAsync(cancellationToken);

        return owed.Sum(one => Takings.OutstandingOf(one.TotalBaht, one.Taken));
    }
}
