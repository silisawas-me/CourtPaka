using System.Security.Claims;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
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

    public static void MapVerifySlipEndpoints(this RouteGroupBuilder venue)
    {
        var slips = venue.MapGroup("/slip-queue")
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.VerifySlip));

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

        var waiting = await database.Bookings
            .AsNoTracking()
            .Where(booking =>
                booking.VenueId == venueId
                && booking.Status == BookingStatus.PendingVerification)
            .Select(booking => new
            {
                booking.Id,
                booking.TotalBaht,
                BookerEmail = booking.Booker!.Email!,
                StartsAt = booking.Slots.Min(slot => slot.StartsAt),
                Latest = database.PaymentSlips
                    .Where(slip => slip.BookingId == booking.Id)
                    .OrderByDescending(slip => slip.UploadedAt)
                    .ThenByDescending(slip => slip.Id)
                    .Select(slip => new { slip.UploadedAt, slip.SameBytesAsSlipId })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var queue = waiting
            .OrderBy(booking => booking.Latest == null ? now : booking.Latest.UploadedAt)
            .Select(booking => new SlipQueueItemResponse(
                booking.Id,
                booking.BookerEmail,
                booking.TotalBaht,
                booking.Latest == null ? now : booking.Latest.UploadedAt,
                booking.StartsAt,
                // Said plainly rather than left to the page to work out, so the venue's view and
                // the platform's reports cannot disagree about what "soon" means.
                booking.StartsAt - now <= SoonToPlay,
                booking.Latest != null && booking.Latest.SameBytesAsSlipId != null))
            .ToArray();

        return TypedResults.Ok(queue);
    }

    /// <summary>
    /// The picture itself. The venue may look at the slips of its own bookings and no others
    /// (PRD 8, PDPA); it is handed over as a download, never rendered in the page's own origin.
    /// </summary>
    private static async Task<Results<FileStreamHttpResult, NotFound, ProblemHttpResult>> SlipAsync(
        Guid venueId,
        Guid bookingId,
        HttpResponse response,
        AppDbContext database,
        ISlipStore slips,
        CancellationToken cancellationToken)
    {
        var slip = await database.PaymentSlips
            .AsNoTracking()
            .Where(candidate =>
                candidate.BookingId == bookingId && candidate.Booking!.VenueId == venueId)
            .OrderByDescending(candidate => candidate.UploadedAt)
            .ThenByDescending(candidate => candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (slip is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, SlipErrorCodes.NoSlip);
        }

        var content = await slips.OpenAsync(slip.StoredName, cancellationToken);
        if (content is null)
        {
            return TypedResults.NotFound();
        }

        response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.File(content, slip.ContentType, fileDownloadName: slip.StoredName);
    }

    /// <summary>
    /// The money is there. The booking is confirmed, and if the hours have already been played it
    /// goes straight on to completed — the venue checking late does not un-play them (PRD 6.1).
    /// </summary>
    private static Task<Results<Ok<BookingResponse>, ProblemHttpResult>> ConfirmAsync(
        Guid venueId,
        Guid bookingId,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        DecideAsync(
            venueId,
            bookingId,
            principal,
            database,
            timeProvider,
            loggers,
            PaymentState.Received,
            reason: null,
            cancellationToken);

    /// <summary>
    /// The venue is turning the booking away. It has to say why, and it has to say whether the
    /// money arrived — that answer is what decides whether anything goes back (PRD 6.1, 6.2).
    /// </summary>
    private static Task<Results<Ok<BookingResponse>, ProblemHttpResult>> RejectAsync(
        Guid venueId,
        Guid bookingId,
        RejectSlipRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        DecideAsync(
            venueId,
            bookingId,
            principal,
            database,
            timeProvider,
            loggers,
            Refunds.StateFor(request.PaymentReceived
                ? Refunds.RejectionOutcome.PaymentReceived
                : Refunds.RejectionOutcome.PaymentNotReceived),
            request.Reason,
            cancellationToken,
            rejecting: true);

    private static async Task<Results<Ok<BookingResponse>, ProblemHttpResult>> DecideAsync(
        Guid venueId,
        Guid bookingId,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        PaymentState payment,
        string? reason,
        CancellationToken cancellationToken,
        bool rejecting = false)
    {
        var now = timeProvider.GetUtcNow();
        var decidedBy = CallerId.Of(principal);

        var booking = await database.Bookings
            .Include(candidate => candidate.Slots)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == bookingId && candidate.VenueId == venueId,
                cancellationToken);

        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        if (booking.Status != BookingStatus.PendingVerification)
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, SlipErrorCodes.NotAwaitingVerification);
        }

        if (rejecting && string.IsNullOrWhiteSpace(reason))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.ReasonRequired);
        }

        if (reason is { Length: > BookingStatusChange.ReasonMaxLength })
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.ReasonTooLong);
        }

        // Added through the set, not the booking's collection: the booking is already tracked and
        // the record carries its own key, so EF would take it for a row that exists and try to
        // update one that does not.
        var decided = rejecting ? BookingStatus.Rejected : BookingStatus.Confirmed;
        var recorded = new List<BookingStatusChange>
        {
            BookingTransitions.Record(
                booking.Id, booking.Status, decided, decidedBy, now, reason?.Trim()),
        };
        booking.Status = decided;
        booking.PaymentState = payment;

        // A booking that was turned away stops holding its hours; they go back on sale (PRD 6.1).
        if (rejecting)
        {
            foreach (var slot in booking.Slots)
            {
                slot.IsActive = false;
            }
        }
        else if (booking.Slots.Max(slot => slot.EndsAt) <= now)
        {
            // Confirmed after the hours were played is played, not upcoming (PRD 6.1).
            recorded.Add(BookingTransitions.Record(
                booking.Id, booking.Status, BookingStatus.Completed, null, now));
            booking.Status = BookingStatus.Completed;
        }

        booking.RefundDueBaht = Refunds.DueFor(booking.Status, booking.PaymentState, booking.TotalBaht);

        database.BookingStatusChanges.AddRange(recorded);
        await database.SaveChangesAsync(cancellationToken);

        var events = AppEvents.For(loggers);
        foreach (var change in recorded)
        {
            events.LogInformation(
                "{Event} {BookingId} {VenueId} {RefundDueBaht}",
                BookingTransitions.EventName(change.To),
                booking.Id,
                venueId,
                booking.RefundDueBaht);
        }

        return TypedResults.Ok(await BookingEndpoints.ReadBookingAsync(
            database, booking.Id, now, cancellationToken));
    }
}
