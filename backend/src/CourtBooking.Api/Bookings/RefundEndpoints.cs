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
///
/// Two acceptance criteria of US-18 are not here. Attaching proof of the transfer waits for the
/// slip store to be something other than a folder on a disk (PRD 9.1), and telling the booker by
/// email waits for US-06 to choose a provider — nothing is delivered today, so writing the send
/// would be writing a promise. Both are named in the PR rather than left to be noticed.
/// </summary>
public static class RefundEndpoints
{
    public static void MapRefundEndpoints(this RouteGroupBuilder bookings)
    {
        // ManageBookings comes from the group this hangs off (VenueBookingEndpoints), and it
        // covers the GET too: the list names amounts and dates against a person (PDPA, PRD 8).
        var refunds = bookings.MapGroup("/{bookingId:guid}/refunds");

        refunds.MapGet("/", ListAsync);
        refunds.MapPost("/", RecordAsync);

        // Voiding is the owner's alone, but the check is in the handler rather than in the policy:
        // the staff who can see the button need to be told which rule stopped them, and a policy
        // answers 403 with nothing for the page to translate.
        refunds.MapPost("/{refundId:guid}/void", VoidAsync);
    }

    /// <summary>
    /// What has been sent back for this booking, and what is still owed. Voided records are here
    /// too: what the venue said at the time is part of the history (PRD US-18).
    /// </summary>
    private static async Task<Results<Ok<RefundsResponse>, ProblemHttpResult>> ListAsync(
        Guid venueId,
        Guid bookingId,
        CurrentVenue venue,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var booking = await FindAsync(database, venueId, bookingId, cancellationToken);
        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        return TypedResults.Ok(await ReadAsync(database, booking, venue.Require(), cancellationToken));
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

        // Baht and satang, which is what the column holds: a third decimal would be rounded on
        // the way in, and then what was checked would not be what was written.
        var amount = Math.Round(request.AmountBaht, 2, MidpointRounding.AwayFromZero);

        if (amount <= 0)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, RefundErrorCodes.AmountNotPositive);
        }

        // What this person may send back at all, before anything is read about the booking:
        // it is a fact about who is asking (PRD US-18). The amount they may is part of the
        // refusal, because the answer is to hand the booker to somebody who can send it.
        var membership = venue.Require();
        if (!RefundLimits.Allows(membership, amount))
        {
            return ApiProblem.Of(
                StatusCodes.Status403Forbidden,
                RefundErrorCodes.OverTheLimit,
                "limitBaht",
                membership.RefundLimitBaht);
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

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        // Everyone writing against this booking queues here. Without it two people — or one
        // double click — both read the same total, both find room for the whole of it, and both
        // write: a booking owing 1,000 ends up with 2,000 recorded against it and drops off the
        // list of what the venue still owes, in records nobody can edit (PRD BR-06).
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock({LockKey(bookingId)})", cancellationToken);

        var outstanding = Refunds.OutstandingOf(
            booking.RefundDueBaht, await SentBackAsync(database, bookingId, cancellationToken));

        if (amount > outstanding)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, RefundErrorCodes.MoreThanIsOwed);
        }

        database.RefundRecords.Add(new RefundRecord
        {
            BookingId = bookingId,
            AmountBaht = amount,
            RefundedOn = request.RefundedOn,
            Method = method,
            Note = string.IsNullOrEmpty(note) ? null : note,
            RecordedByUserId = membership.UserId,
            RecordedAt = timeProvider.GetUtcNow(),
        });

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "refund_recorded {BookingId} {VenueId} {AmountBaht} {Method}",
            bookingId, venueId, amount, method);

        return TypedResults.Ok(await ReadAsync(database, booking, venue.Require(), cancellationToken));
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

        // The void is allowed — a record of money that never went is worse than a debt — but a
        // debt to somebody who has since asked to be forgotten is one nobody can pay without the
        // platform stepping in, so it is said out loud for it (PDPA, PRD 8, S-15).
        if (await database.Bookings.AnyAsync(
                one => one.Id == bookingId && one.Booker != null && one.Booker.DeletedAt != null,
                cancellationToken))
        {
            loggers.CreateLogger("CourtBooking.Refunds").LogWarning(
                "Refund {RefundId} on booking {BookingId} was voided after its booker deleted the "
                + "account; the amount is owed to somebody nobody can reach.",
                refundId, bookingId);
        }

        return TypedResults.Ok(await ReadAsync(database, booking, venue.Require(), cancellationToken));
    }

    /// <summary>
    /// One number per booking, so that everybody writing against the same one waits for the
    /// others rather than all of them reading the same total. Sharing a number with something
    /// else only means queueing behind it, which costs a moment and nothing else.
    /// </summary>
    private static long LockKey(Guid bookingId)
    {
        Span<byte> id = stackalloc byte[16];
        bookingId.TryWriteBytes(id);
        return BitConverter.ToInt64(id[..8]) ^ BitConverter.ToInt64(id[8..]);
    }

    /// <summary>What counts towards what has been sent back: every record that still stands.</summary>
    private static async Task<decimal> SentBackAsync(
        AppDbContext database,
        Guid bookingId,
        CancellationToken cancellationToken) =>
        await database.RefundRecords
            .StillStanding()
            .Where(record => record.BookingId == bookingId)
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
        VenueMembership member,
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
            [.. records],
            member.RefundCeiling);
    }
}
