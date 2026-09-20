using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// What the venue has actually sent back (PRD US-18, BR-06).
///
/// The money moves outside this system, so nothing here moves any; these are records of transfers
/// that have already happened. They are written once and never edited — a record that can be
/// changed is not evidence — and the only thing that may happen to one afterwards is the owner
/// voiding it with a reason, which puts the amount back on what the venue still owes.
/// </summary>
public static class RefundEndpoints
{
    public static void MapRefundEndpoints(this RouteGroupBuilder bookings)
    {
        var refunds = bookings.MapGroup("/{bookingId:guid}/refunds")
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageBookings));

        refunds.MapGet("/", ListAsync);
        refunds.MapPost("/", RecordAsync);
        refunds.MapPost("/{refundId:guid}/void", VoidAsync);
    }

    /// <summary>
    /// What has been sent back for this booking, and what is still owed. Voided records are here
    /// too: what the venue said at the time is part of the history (PRD US-18).
    /// </summary>
    private static async Task<Results<Ok<RefundsResponse>, ProblemHttpResult>> ListAsync(
        Guid venueId,
        Guid bookingId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var booking = await FindAsync(database, venueId, bookingId, cancellationToken);
        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        return TypedResults.Ok(await ReadAsync(database, booking, cancellationToken));
    }

    /// <summary>
    /// Writing down a transfer the venue has made. It may be written in parts until the whole of
    /// what is owed has been sent, and never for more than that (PRD US-18).
    /// </summary>
    private static async Task<Results<Ok<RefundsResponse>, ProblemHttpResult>> RecordAsync(
        Guid venueId,
        Guid bookingId,
        RecordRefundRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<RefundMethod>(request.Method, out var method)
            || !Enum.GetNames<RefundMethod>().Contains(request.Method, StringComparer.Ordinal))
        {
            return ApiProblem.Of(
                StatusCodes.Status400BadRequest, RefundErrorCodes.MethodNotAllowed);
        }

        var note = request.Note?.Trim();
        if (note is { Length: > RefundRecord.NoteMaxLength })
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, RefundErrorCodes.NoteTooLong);
        }

        var booking = await FindAsync(database, venueId, bookingId, cancellationToken);
        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        // A day in the future is not a transfer that has happened, which is all this records.
        if (request.RefundedOn > PlatformRequirements.BangkokToday(timeProvider))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, RefundErrorCodes.NotYetSent);
        }

        var outstanding = Refunds.OutstandingOf(
            booking.RefundDueBaht, await SentBackAsync(database, bookingId, cancellationToken));

        if (request.AmountBaht <= 0)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, RefundErrorCodes.AmountNotPositive);
        }

        if (request.AmountBaht > outstanding)
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, RefundErrorCodes.MoreThanIsOwed);
        }

        database.RefundRecords.Add(new RefundRecord
        {
            BookingId = bookingId,
            AmountBaht = request.AmountBaht,
            RefundedOn = request.RefundedOn,
            Method = method,
            Note = string.IsNullOrEmpty(note) ? null : note,
            RecordedByUserId = venue.Require().UserId,
            RecordedAt = timeProvider.GetUtcNow(),
        });

        await database.SaveChangesAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "refund_recorded {BookingId} {VenueId} {AmountBaht} {Method}",
            bookingId, venueId, request.AmountBaht, method);

        return TypedResults.Ok(await ReadAsync(database, booking, cancellationToken));
    }

    /// <summary>
    /// Taking a record back — it was typed wrongly, or the transfer bounced. The owner's alone,
    /// and it has to say why, because the amount goes back onto what the venue owes (PRD US-18).
    /// </summary>
    private static async Task<Results<Ok<RefundsResponse>, ProblemHttpResult>> VoidAsync(
        Guid venueId,
        Guid bookingId,
        Guid refundId,
        VoidRefundRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();
        if (membership.Role != VenueRole.Owner)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, BookingErrorCodes.OwnerOnly);
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, BookingErrorCodes.ReasonRequired);
        }

        if (reason.Length > RefundRecord.NoteMaxLength)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, RefundErrorCodes.NoteTooLong);
        }

        var booking = await FindAsync(database, venueId, bookingId, cancellationToken);
        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        // Conditional, so two people voiding at once do not both write a reason over the record.
        var voided = await database.RefundRecords
            .Where(record =>
                record.Id == refundId
                && record.BookingId == bookingId
                && record.VoidedAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(record => record.VoidedAt, timeProvider.GetUtcNow())
                    .SetProperty(record => record.VoidedByUserId, membership.UserId)
                    .SetProperty(record => record.VoidReason, reason),
                cancellationToken);

        if (voided == 0)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, RefundErrorCodes.AlreadyVoided);
        }

        AppEvents.For(loggers).LogInformation(
            "refund_voided {BookingId} {VenueId} {RefundId}", bookingId, venueId, refundId);

        return TypedResults.Ok(await ReadAsync(database, booking, cancellationToken));
    }

    /// <summary>What counts towards what has been sent back: every record that still stands.</summary>
    internal static async Task<decimal> SentBackAsync(
        AppDbContext database,
        Guid bookingId,
        CancellationToken cancellationToken) =>
        await database.RefundRecords
            .Where(record => record.BookingId == bookingId && record.VoidedAt == null)
            .SumAsync(record => record.AmountBaht, cancellationToken);

    private static Task<Booking?> FindAsync(
        AppDbContext database,
        Guid venueId,
        Guid bookingId,
        CancellationToken cancellationToken) =>
        database.Bookings
            .AsNoTracking()
            .SingleOrDefaultAsync(
                booking => booking.Id == bookingId && booking.VenueId == venueId,
                cancellationToken);

    private static async Task<RefundsResponse> ReadAsync(
        AppDbContext database,
        Booking booking,
        CancellationToken cancellationToken)
    {
        var records = await database.RefundRecords
            .AsNoTracking()
            .Where(record => record.BookingId == booking.Id)
            .OrderBy(record => record.RefundedOn)
            .ThenBy(record => record.RecordedAt)
            .Select(record => new RefundRecordResponse(
                record.Id,
                record.AmountBaht,
                record.RefundedOn,
                record.Method.ToString(),
                record.Note,
                record.RecordedAt,
                record.VoidedAt,
                record.VoidReason))
            .ToListAsync(cancellationToken);

        var sentBack = records
            .Where(record => record.VoidedAt is null)
            .Sum(record => record.AmountBaht);

        return new RefundsResponse(
            booking.RefundDueBaht,
            sentBack,
            Refunds.OutstandingOf(booking.RefundDueBaht, sentBack),
            [.. records]);
    }
}
