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
    /// <summary>What somebody bought across the counter, where that is what it was (US-32).</summary>
    Guid? SaleId,
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

/// <summary>
/// A count of the drawer. <c>EndsDay</c> false is a shift handing over (thai-fit T2); true, the
/// default, closes the venue's day as every count used to.
/// </summary>
public sealed record CloseDayRequest(
    decimal OpeningFloatBaht,
    decimal CountedCashBaht,
    string? Note,
    bool EndsDay = true);

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
    /// <summary>
    /// Cash the venue handed back that day: sent to a booker (PRD US-18) or over the counter for a
    /// sale taken back (US-32). Both leave the till, and both are money going back to somebody.
    /// </summary>
    decimal CashRefundedBaht,
    /// <summary>
    /// Cash the venue paid out that day (PRD US-33). Out of the same drawer, but not money going
    /// back to anybody — a delivery, the water bill — so it is counted and shown on its own.
    /// </summary>
    decimal CashPaidOutBaht,
    decimal OutstandingBaht,
    PaymentReceiptResponse[] CashReceipts,
    DailyClosingResponse? Closed,
    /// <summary>
    /// Where the difference might have come from, once the day has been counted and did not come
    /// out even. Empty until then, and empty when nothing matches (PRD US-26).
    /// </summary>
    MoneyLeadResponse[] Leads,
    /// <summary>Into the bank account directly, and TrueMoney (thai-fit T3).</summary>
    decimal BankTransferBaht = 0,
    decimal TrueMoneyBaht = 0,
    /// <summary>Every count of the day so far, shifts and the close, in the order they were made.</summary>
    DailyClosingResponse[]? Counts = null,
    /// <summary>The drawer since the last count: what the shift now open has taken and paid out.</summary>
    OpenShiftResponse? OpenShift = null,
    /// <summary>Every movement of the day's money, in and out, as the drawer page lists it.</summary>
    MoneyLineResponse[]? Lines = null,
    /// <summary>When the venue opened that day, which is where its first shift is said to start.</summary>
    int? OpensHour = null);

/// <summary>
/// The shift still running (thai-fit T2): from the last count (or the start of the venue's day)
/// to now. Its expected cash is the float the shift was handed plus these, so the screen shows
/// what the server will check without working it out.
/// </summary>
public sealed record OpenShiftResponse(DateTimeOffset From, decimal CashInBaht, decimal CashOutBaht);

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
    DateTimeOffset ClosedAt,
    /// <summary>True for the count that closed the day; false for a shift handing over.</summary>
    bool EndsDay = true,
    /// <summary>Who counted, by the name the counter knows them by.</summary>
    string? ClosedBy = null,
    /// <summary>Where this count's shift started: the count before it, or the start of the day.</summary>
    DateTimeOffset? From = null);

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
            CountsOn = await CountsOnAsync(database, venueId, now, cancellationToken),
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
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var day = date ?? VenueClock.Today(timeProvider, venue.DayStartsHour);
        var (from, until) = await Takings.TillDayAsync(database, venueId, day, cancellationToken);

        var receipts = await database.PaymentReceipts
            .AsNoTracking()
            .Where(receipt => receipt.VenueId == venueId && receipt.CountsOn == day)
            .OrderBy(receipt => receipt.ReceivedAt)
            .Select(receipt => new
            {
                receipt.Id,
                receipt.BookingId,
                receipt.PackageId,
                receipt.SaleId,
                receipt.AmountBaht,
                receipt.Method,
                receipt.ReceivedAt,
                receipt.Note,
            })
            .ToListAsync(cancellationToken);

        var cashOut = await CashOutAsync(database, venueId, from, until, cancellationToken);

        // What the day's bookings are still short, counted the same way the rows are.
        var owing = await OwingOnAsync(database, venueId, day, cancellationToken);
        var outstanding = owing.Sum(one => one.Baht);

        // Every count of the day, shifts first and the close last (thai-fit T2). Each covers the
        // drawer from the count before it, so the list reads as the day handed from hand to hand.
        var rows = await database.DailyClosings
            .AsNoTracking()
            .Where(closing => closing.VenueId == venueId && closing.Date == day)
            .OrderBy(closing => closing.ClosedAt)
            .Select(closing => new
            {
                Closing = closing,
                By = closing.ClosedBy!.DeletedAt == null
                    ? closing.ClosedBy.DisplayName ?? closing.ClosedBy.Email
                    : null,
            })
            .ToListAsync(cancellationToken);

        var counts = rows
            .Select((row, index) => new DailyClosingResponse(
                row.Closing.Date,
                row.Closing.OpeningFloatBaht,
                row.Closing.ExpectedCashBaht,
                row.Closing.CountedCashBaht,
                row.Closing.DifferenceBaht,
                row.Closing.Note,
                row.Closing.ClosedAt,
                row.Closing.EndsDay,
                row.By,
                index == 0 ? from : rows[index - 1].Closing.ClosedAt))
            .ToArray();

        var closed = counts.SingleOrDefault(count => count.EndsDay);

        // The shift still open: from the last count to the end of the day's window. Once the day
        // is closed there is no open shift — what comes in now is tomorrow's.
        OpenShiftResponse? openShift = null;
        if (closed is null)
        {
            var shiftFrom = counts.Length > 0 ? counts[^1].ClosedAt : from;
            openShift = new OpenShiftResponse(
                shiftFrom,
                receipts
                    .Where(receipt => receipt.Method == PaymentMethod.Cash && receipt.ReceivedAt > shiftFrom)
                    .Sum(receipt => receipt.AmountBaht),
                cashOut.Where(one => one.At > shiftFrom).Sum(one => one.AmountBaht));
        }

        decimal By(PaymentMethod method) =>
            receipts.Where(receipt => receipt.Method == method).Sum(receipt => receipt.AmountBaht);

        decimal Out(CashOutKind kind) =>
            cashOut.Where(one => one.Kind == kind).Sum(one => one.AmountBaht);

        PaymentReceiptResponse[] cashTaken =
        [
            .. receipts
                .Where(receipt => receipt.Method == PaymentMethod.Cash)
                .Select(receipt => new PaymentReceiptResponse(
                    receipt.Id,
                    receipt.BookingId,
                    receipt.PackageId,
                    receipt.SaleId,
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
            Out(CashOutKind.Refunded) + Out(CashOutKind.SaleTakenBack),
            Out(CashOutKind.PaidOut),
            outstanding,
            cashTaken,
            closed,
            // Leads answer the latest count, shift or close: that is the drawer just counted.
            LeadsFor(counts.LastOrDefault(), cashTaken, cashOut, owing),
            By(PaymentMethod.BankTransfer),
            By(PaymentMethod.TrueMoney),
            counts,
            openShift,
            await MoneyLines.ForDayAsync(database, venueId, day, cashOut, cancellationToken),
            (await CourtEndpoints.ScheduleOnAsync(database, venueId, day, cancellationToken))?
                .Days.SingleOrDefault(one => one.Day == day.DayOfWeek)?.OpensHour));
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
        IReadOnlyList<CashOut> cashOut,
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

            .. cashOut
                .Where(one => Takings.Explains(one.AmountBaht, difference))
                .Select(one => new MoneyLeadResponse(
                    one.Kind == CashOutKind.PaidOut
                        ? nameof(MoneyLeadKind.CashPaidOut)
                        : nameof(MoneyLeadKind.CashHandedBack),
                    one.AmountBaht,
                    one.BookingId,
                    one.At,
                    one.Note)),

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

    /// <summary>
    /// Every lot of cash that left this venue's drawer in the window, whichever door it left by
    /// (PRD US-18, US-32, US-33). The one place that knows the doors: the count subtracts these
    /// and the page shows them, so neither can be looking at money the other is not.
    ///
    /// All three are counted on the day the notes left the drawer — the day the refund was written
    /// down, the day the expense was recorded, the day the sale was taken back — and not on any
    /// date typed beside them. The drawer is short from the moment the money goes.
    /// </summary>
    private static async Task<List<CashOut>> CashOutAsync(
        AppDbContext database,
        Guid venueId,
        DateTimeOffset from,
        DateTimeOffset until,
        CancellationToken cancellationToken)
    {
        var refunded = await database.RefundRecords
            .StillStanding()
            .Where(record =>
                record.Booking!.VenueId == venueId
                && record.Method == RefundMethod.Cash
                && record.RecordedAt >= from
                && record.RecordedAt < until)
            .Select(record => new CashOut(
                CashOutKind.Refunded,
                record.AmountBaht,
                record.RecordedAt,
                record.BookingId,
                null,
                null,
                record.RecordedByUserId))
            .ToListAsync(cancellationToken);

        var paidOut = await database.Spends
            .AsNoTracking()
            .Where(spend =>
                spend.VenueId == venueId
                && spend.VoidedAt == null
                && spend.PaidBy == PaymentMethod.Cash
                && spend.RecordedAt >= from
                && spend.RecordedAt < until)
            .Select(spend => new CashOut(
                CashOutKind.PaidOut,
                spend.AmountBaht,
                spend.RecordedAt,
                null,
                spend.Note,
                spend.Kind,
                spend.RecordedByUserId))
            .ToListAsync(cancellationToken);

        // A sale taken back hands the money back across the counter. Nothing was bought, so there
        // is no expense to write for it — the rows that already say it happened are what the
        // drawer reads: the sale's own cancellation, and the receipt it was paid with (PRD US-32).
        var handedBack = await database.ShopSales
            .AsNoTracking()
            .Where(sale =>
                sale.VenueId == venueId
                && sale.CancelledAt >= from
                && sale.CancelledAt < until)
            .SelectMany(sale => database.PaymentReceipts
                .Where(receipt =>
                    receipt.SaleId == sale.Id && receipt.Method == PaymentMethod.Cash)
                .Select(receipt => new CashOut(
                    CashOutKind.SaleTakenBack,
                    receipt.AmountBaht,
                    sale.CancelledAt!.Value,
                    null,
                    sale.CancelReason)))
            .ToListAsync(cancellationToken);

        return [.. refunded, .. paidOut, .. handedBack];
    }

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
        var today = VenueClock.Today(timeProvider, venue.DayStartsHour);
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

        var (from, until) = await Takings.TillDayAsync(database, venueId, day, cancellationToken);

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        // Nothing may be added to this day between what is counted here and the count being
        // written: money that arrived while the drawer was being counted belongs to tomorrow,
        // and the receipt decides that by asking whether this row is already there (PRD US-26).
        await QueueForTheDayAsync(database, venueId, day, cancellationToken);

        // Read inside the queue: a shift counted a moment ago moves where this one starts, and a
        // day already closed takes no more counts (thai-fit T2).
        var earlier = await database.DailyClosings
            .Where(closing => closing.VenueId == venueId && closing.Date == day)
            .Select(closing => new { closing.ClosedAt, closing.EndsDay })
            .ToListAsync(cancellationToken);
        if (earlier.Any(one => one.EndsDay))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status409Conflict, MoneyErrorCodes.AlreadyClosed);
        }

        // This count covers the drawer since the last one, or since the day began.
        DateTimeOffset? since = earlier.Count > 0 ? earlier.Max(one => one.ClosedAt) : null;

        var cashIn = await database.PaymentReceipts
            .Where(receipt =>
                receipt.VenueId == venueId
                && receipt.Method == PaymentMethod.Cash
                && receipt.CountsOn == day
                && (since == null || receipt.ReceivedAt > since))
            .SumAsync(receipt => (decimal?)receipt.AmountBaht, cancellationToken) ?? 0m;

        var cashOut = (await CashOutAsync(database, venueId, from, until, cancellationToken))
            .Where(one => since == null || one.At > since)
            .ToList();

        var expected = Takings.ExpectedCash(
            request.OpeningFloatBaht, cashIn, cashOut.Sum(one => one.AmountBaht));
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
            EndsDay = request.EndsDay,
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
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status409Conflict, MoneyErrorCodes.AlreadyClosed);
        }

        await transaction.CommitAsync(cancellationToken);

        // A shift handing over is not the day closing; the event of PRD 8.1 is the day's.
        if (closing.EndsDay)
        {
            AppEvents.For(loggers).LogInformation(
                "day_closed {VenueId} {Date} {Difference}", venueId, day, closing.DifferenceBaht);
        }

        return TypedResults.Ok(new DailyClosingResponse(
            closing.Date,
            closing.OpeningFloatBaht,
            closing.ExpectedCashBaht,
            closing.CountedCashBaht,
            closing.DifferenceBaht,
            closing.Note,
            closing.ClosedAt,
            closing.EndsDay,
            null,
            since ?? from));
    }


    /// <summary>What has been taken for one booking so far.</summary>
    /// <summary>
    /// Which of the venue's days this money is counted in, and a place in the queue behind
    /// anyone counting that day (PRD US-26).
    ///
    /// Both halves matter together: reading whether the day is closed and writing the receipt
    /// have to be one decision, or a receipt written while the count is being taken lands in a
    /// day whose total was read a moment before it — and the till comes up short by exactly that
    /// receipt, with nothing to explain it.
    /// </summary>
    internal static async Task<DateOnly> CountsOnAsync(
        AppDbContext database,
        Guid venueId,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        // The venue's day: cash taken at 01:00 Saturday by a venue open until 02:00 is Friday's.
        var receivedOn = VenueClock.DayAndHour(
            receivedAt, await VenueDay.DayStartsHourAsync(database, venueId, cancellationToken)).Date;
        await QueueForTheDayAsync(database, venueId, receivedOn, cancellationToken);

        // Only the close of the day sends money to tomorrow; a shift's count does not (thai-fit T2).
        var counted = await database.DailyClosings
            .AnyAsync(
                closing => closing.VenueId == venueId && closing.Date == receivedOn && closing.EndsDay,
                cancellationToken);

        return Takings.CountsOn(receivedOn, counted);
    }

    /// <summary>
    /// Queues behind anybody else counting or adding to this venue's day, so a count and a
    /// receipt cannot pass each other (PRD US-26). One number per venue per day, the same for
    /// everyone asking for it.
    /// </summary>
    internal static Task QueueForTheDayAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly day,
        CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({venueId.ToString() + day.ToString("O")}, 0))",
            cancellationToken);

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
        await Locks.OnAsync(database, bookingId, cancellationToken);

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
                receipt.SaleId,
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
        var (from, until) = VenueClock.Window(
            day, await VenueDay.DayStartsHourAsync(database, venueId, cancellationToken));

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
