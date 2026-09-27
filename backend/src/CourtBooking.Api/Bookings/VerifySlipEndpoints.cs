using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// The venue's side of a payment: the queue of slips waiting to be looked at, and the two answers
/// (PRD US-12). Nothing here decides whether money arrived — a person does, and this records what
/// they decided.
/// </summary>
public static class VerifySlipEndpoints
{
    /// <summary>A booking closer than this to its first hour is worth looking at first (PRD US-12).</summary>
    public static readonly TimeSpan SoonToPlay = TimeSpan.FromHours(1);

    /// <summary>
    /// How much of the queue one answer carries. A venue that stops checking never has its queue
    /// emptied for it — PRD 6.1 gives PendingVerification no timeout — so without a ceiling the
    /// response grows for as long as the venue neglects it.
    /// </summary>
    public const int MaxWaiting = 200;

    /// <summary>
    /// The fields an event line carries, one line per status, so that a sink grouping by template
    /// sees these as the different events they are (PRD 8). The names come from
    /// <see cref="BookingTransitions.EventName"/>, which is where the platform's event names live.
    /// </summary>
    private static readonly Dictionary<BookingStatus, string> Announcements =
        new[] { BookingStatus.Confirmed, BookingStatus.Rejected, BookingStatus.Completed }
            .ToDictionary(
                status => status,
                status => BookingTransitions.EventName(status)
                    + " {BookingId} {VenueId} {RefundDueBaht}");

    public static void MapVerifySlipEndpoints(this RouteGroupBuilder venue)
    {
        // Reading is behind the permission too, not merely behind membership: a slip is someone's
        // bank account, and PRD 8 gives it to the people holding this permission rather than to
        // the venue's members at large (PDPA). A frozen venue therefore cannot open its queue at
        // all, which is meant: a venue that may not take money should not be checking payments.
        var slips = venue.MapGroup("/slip-queue")
            // A suspension stops a venue taking money, not looking at money it was already
            // sent: a slip in this queue is somebody's transfer, waiting (PRD US-20).
            .RequireAuthorization(
                VenuePolicies.NeedsEvenWhenSuspended(VenuePermissions.VerifySlip));

        slips.MapGet("/", QueueAsync);
        slips.MapGet("/{bookingId:guid}/slip", SlipAsync);
        slips.MapPost("/{bookingId:guid}/confirm", ConfirmAsync);
        slips.MapPost("/{bookingId:guid}/reject", RejectAsync);
    }

    /// <summary>
    /// What is waiting, oldest first — the order a queue is worked through, and the order that
    /// keeps the booker who has waited longest from waiting longer still (PRD US-12).
    /// </summary>
    private static async Task<Ok<SlipQueueItemResponse[]>> QueueAsync(
        Guid venueId,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // The ordering is spelled out here rather than taken from SlipDownload.NewestFirst: this
        // one is inside the projection, where the database does the composing and a method of
        // ours would have nothing to translate to.
        var waiting = await database.Bookings
            .AsNoTracking()
            .Where(booking =>
                booking.VenueId == venueId
                && booking.Status == BookingStatus.PendingVerification)
            .Select(booking => new
            {
                booking.Id,
                booking.TotalBaht,
                booking.DepositBaht,
                BookerEmail = booking.Booker!.Email,
                BookerPhone = booking.Booker.PhoneNumber,
                StartsAt = booking.Slots.Min(slot => slot.StartsAt),
                Latest = database.PaymentSlips
                    .Where(slip => slip.BookingId == booking.Id)
                    .OrderByDescending(slip => slip.UploadedAt)
                    .ThenByDescending(slip => slip.Id)
                    .Select(slip => new { slip.UploadedAt, slip.SameBytesAsSlipId })
                    .FirstOrDefault(),
            })
            // A booking reaches this queue by a slip arriving, so there is always one to sort by,
            // and sorting in the database is what makes the ceiling above mean the oldest.
            .OrderBy(booking => booking.Latest!.UploadedAt)
            .Take(MaxWaiting)
            .ToListAsync(cancellationToken);

        var queue = waiting
            .Select(booking => new SlipQueueItemResponse(
                booking.Id,
                booking.BookerEmail,
                // The one way left to reach a booker who has no address here (PRD US-01).
                booking.BookerEmail is null ? booking.BookerPhone : null,
                booking.TotalBaht,
                booking.DepositBaht,
                booking.Latest!.UploadedAt,
                booking.StartsAt,
                // Said plainly rather than left to the page to work out, so the venue's view and
                // the platform's reports cannot disagree about what "soon" means.
                booking.StartsAt - now <= SoonToPlay,
                booking.Latest.SameBytesAsSlipId != null))
            .ToArray();

        return TypedResults.Ok(queue);
    }

    /// <summary>
    /// The picture itself. The venue may look at the slips of its own bookings and no others
    /// (PRD 8, PDPA).
    /// </summary>
    private static Task<Results<FileStreamHttpResult, NotFound, ProblemHttpResult>> SlipAsync(
        Guid venueId,
        Guid bookingId,
        HttpResponse response,
        AppDbContext database,
        ISlipStore slips,
        CancellationToken cancellationToken) =>
        SlipDownload.NewestAsync(
            database.PaymentSlips.Where(candidate =>
                candidate.BookingId == bookingId && candidate.Booking!.VenueId == venueId),
            slips,
            response,
            cancellationToken);

    /// <summary>
    /// The money is there. The booking is confirmed, and if the hours have already been played it
    /// goes straight on to completed — the venue checking late does not un-play them (PRD 6.1).
    /// </summary>
    private static Task<Results<Ok<BookingResponse>, ProblemHttpResult>> ConfirmAsync(
        Guid venueId,
        Guid bookingId,
        ConfirmSlipRequest? request,
        CurrentVenue venue,
        VenueNotifications notifications,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        DecideAsync(
            venueId,
            bookingId,
            BookingStatus.Confirmed,
            PaymentState.Received,
            reason: null,
            request?.AmountBaht,
            venue,
            notifications,
            database,
            timeProvider,
            loggers,
            cancellationToken);

    /// <summary>
    /// The venue is turning the booking away. It has to say why, and it has to say whether the
    /// money arrived — that answer is what decides whether anything goes back (PRD 6.1, 6.2).
    /// </summary>
    private static Task<Results<Ok<BookingResponse>, ProblemHttpResult>> RejectAsync(
        Guid venueId,
        Guid bookingId,
        RejectSlipRequest request,
        CurrentVenue venue,
        VenueNotifications notifications,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        DecideAsync(
            venueId,
            bookingId,
            BookingStatus.Rejected,
            request.PaymentReceived ? PaymentState.Received : PaymentState.NotReceived,
            request.Reason,
            // A rejection records what arrived so it can be sent back; the venue read it off the
            // slip in front of it, and there is nowhere else to ask.
            request.AmountBaht,
            venue,
            notifications,
            database,
            timeProvider,
            loggers,
            cancellationToken);

    private static async Task<Results<Ok<BookingResponse>, ProblemHttpResult>> DecideAsync(
        Guid venueId,
        Guid bookingId,
        BookingStatus decided,
        PaymentState payment,
        string? reason,
        decimal? amountBaht,
        CurrentVenue venue,
        VenueNotifications notifications,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var decidedBy = venue.Require().UserId;

        var booking = await database.Bookings
            .AsNoTracking()
            .Where(candidate => candidate.Id == bookingId && candidate.VenueId == venueId)
            .Select(candidate => new
            {
                candidate.TotalBaht,
                candidate.DepositBaht,
                // Null where no package paid for it; zero is a different answer (PRD US-31).
                PaidWithHours = candidate.PackageId == null ? (decimal?)null : candidate.PackageBaht,
                LastHourEndsAt = candidate.Slots.Max(slot => slot.EndsAt),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        // Trimmed before it is measured as well as before it is kept, so that what the length is
        // judged on is what would be stored.
        var written = reason?.Trim();

        if (decided == BookingStatus.Rejected && string.IsNullOrEmpty(written))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.ReasonRequired);
        }

        if (written is { Length: > BookingStatusChange.ReasonMaxLength })
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.ReasonTooLong);
        }

        // Confirmed after the hours were played is played, not upcoming (PRD 6.1).
        var landed = BookingTransitions.LandingFor(decided, booking.LastHourEndsAt, now);

        // A rejection gives back everything; a confirmation gives back nothing. The share is
        // written down so that a later answer about the money lands on the same number (PRD 6.2).
        var refundPercent = decided == BookingStatus.Rejected ? Refunds.AllOfIt : 0;

        // What the venue is holding before this decision writes anything: a deposit taken at the
        // desk, or an earlier slip. A booking turned away gives back what arrived, which is not
        // always its price (PRD US-28).
        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        // Behind everybody else about to read what this booking has had paid against it: the desk
        // can take part of a deposit booking without moving its status, so the conditional update
        // below would not catch it and the receipt written here would count the same money twice
        // (PRD US-26).
        await CounterMoneyEndpoints.QueueForTheMoneyAsync(database, bookingId, cancellationToken);

        var taken = await CounterMoneyEndpoints.TakenAsync(database, bookingId, cancellationToken);

        // What the slip was for: the deposit, less anything already taken against it. For a venue
        // that asks for the whole price — which is every venue until one says otherwise — this is
        // the price, and everything below reads as it always has (PRD US-28).
        // What the venue says arrived. Left unsaid, it is what was asked for — the amount the
        // booker's code was made out for. A venue that can see its own account is the only one who
        // can say otherwise, and a transfer of more than the deposit has to be recorded as what it
        // was or the desk asks for it twice (PRD US-28).
        var owing = Takings.OutstandingOf(booking.TotalBaht, taken);
        var arrived = amountBaht is { } said
            ? decimal.Round(said, 2, MidpointRounding.AwayFromZero)
            : Takings.OutstandingOf(booking.DepositBaht, taken);

        if (amountBaht is not null && (arrived <= 0m || arrived > owing))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.InvalidAmount);
        }

        var transferred = payment == PaymentState.Received ? arrived : 0m;

        // `Received` means the venue has all of it. A booking held on a deposit is confirmed with
        // its balance still to come, so it says NotReceived and the desk's door stays open for the
        // rest (PRD US-26). What the venue actually holds is the receipts, not this.
        var holding = taken + transferred;
        var settled = payment == PaymentState.Received && holding < booking.TotalBaht
            ? PaymentState.NotReceived
            : payment;

        var refundDue = Refunds.DueFor(
            landed,
            settled,
            booking.TotalBaht,
            refundPercent,
            holding,
            booking.DepositBaht,
            booking.PaidWithHours);

        // A booking that was turned away stops holding its hours; they go back on sale (PRD 6.1).
        // Before the booking row, not after: that is the order every writer takes these two
        // tables in, and the rollback below unwinds this with it.
        if (decided == BookingStatus.Rejected)
        {
            await BookedSlots.ReleaseAsync(database, bookingId, cancellationToken);
        }

        // Two people can be working one queue, and the status was read a moment ago. PRD 6.1 asks
        // for the condition and the move to be decided together, so the move carries the
        // condition: it changes nothing unless the booking is still waiting to be checked.
        // Without it a confirm landing just after a reject would leave a booking confirmed whose
        // hours had already gone back on sale — and the constraint that stops double booking reads
        // only those hours, so the court would then be sold twice over (PRD BR-04).
        var moved = await database.Bookings
            .Where(candidate =>
                candidate.Id == bookingId
                && candidate.VenueId == venueId
                && candidate.Status == BookingStatus.PendingVerification
                // The price is no longer fixed once a booking is made: an evening that runs on
                // raises it (PRD US-29). Every amount below was worked out from the price read
                // before this transaction, so the write carries that price as its condition —
                // an hour added in between leaves this changing nothing, and the venue is told
                // to look again rather than settling money against a total that has moved.
                && candidate.TotalBaht == booking.TotalBaht)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(candidate => candidate.Status, landed)
                    .SetProperty(candidate => candidate.PaymentState, settled)
                    .SetProperty(candidate => candidate.RefundPercent, refundPercent)
                    .SetProperty(candidate => candidate.RefundDueBaht, refundDue),
                cancellationToken);

        if (moved == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, SlipErrorCodes.NotAwaitingVerification);
        }

        var recorded = BookingTransitions.RecordsFor(
            bookingId, BookingStatus.PendingVerification, decided, landed, decidedBy, now, written);

        database.BookingStatusChanges.AddRange(recorded);

        // A slip the venue accepted is money that arrived, so the day's count knows about it
        // (PRD US-26). Written here rather than left to the counter: nobody types in what a
        // booker transferred, and a day whose transfers are missing is a day that does not add up.
        //
        // What it covers is whatever the desk has not already taken — a deposit paid at the
        // counter and the rest transferred is two receipts, not one of them overwritten and not
        // a booking whose receipts never reach its price.
        if (transferred > 0)
        {
            database.PaymentReceipts.Add(new PaymentReceipt
            {
                BookingId = bookingId,
                VenueId = venueId,
                CountsOn = await CounterMoneyEndpoints.CountsOnAsync(
                    database, venueId, now, cancellationToken),
                AmountBaht = transferred,
                Method = PaymentMethod.PromptPay,
                ReceivedAt = now,
                // The booker sent it; the person at the desk only agreed that it arrived.
                ReceivedByUserId = null,
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var events = AppEvents.For(loggers);
        foreach (var change in recorded)
        {
            events.LogInformation(Announcements[change.To], bookingId, venueId, refundDue);
        }

        // Turning a booking away after the money arrived leaves the venue owing it back, and
        // nothing else would say so (PRD US-17, BR-06).
        await notifications.MoneyMayBeWaitingAsync(venueId, bookingId, refundDue, settled);

        return TypedResults.Ok(await BookingEndpoints.ReadBookingAsync(
            database, bookingId, now, cancellationToken));
    }
}
