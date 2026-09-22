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
        var landed = decided == BookingStatus.Confirmed && booking.LastHourEndsAt <= now
            ? BookingStatus.Completed
            : decided;

        // A rejection gives back everything; a confirmation gives back nothing. The share is
        // written down so that a later answer about the money lands on the same number (PRD 6.2).
        var refundPercent = decided == BookingStatus.Rejected ? Refunds.AllOfIt : 0;
        var refundDue = Refunds.DueFor(landed, payment, booking.TotalBaht, refundPercent);

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

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
                && candidate.Status == BookingStatus.PendingVerification)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(candidate => candidate.Status, landed)
                    .SetProperty(candidate => candidate.PaymentState, payment)
                    .SetProperty(candidate => candidate.RefundPercent, refundPercent)
                    .SetProperty(candidate => candidate.RefundDueBaht, refundDue),
                cancellationToken);

        if (moved == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, SlipErrorCodes.NotAwaitingVerification);
        }

        var recorded = new List<BookingStatusChange>
        {
            BookingTransitions.Record(
                bookingId, BookingStatus.PendingVerification, decided, decidedBy, now, written),
        };

        if (landed != decided)
        {
            // Nobody pressed this one; the clock did (PRD 6.1), so it is recorded with no actor.
            recorded.Add(BookingTransitions.Record(bookingId, decided, landed, null, now));
        }

        database.BookingStatusChanges.AddRange(recorded);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var events = AppEvents.For(loggers);
        foreach (var change in recorded)
        {
            events.LogInformation(Announcements[change.To], bookingId, venueId, refundDue);
        }

        // Turning a booking away after the money arrived leaves the venue owing it back, and
        // nothing else would say so (PRD US-17, BR-06).
        await notifications.MoneyMayBeWaitingAsync(venueId, bookingId, refundDue, payment);

        return TypedResults.Ok(await BookingEndpoints.ReadBookingAsync(
            database, bookingId, now, cancellationToken));
    }
}
