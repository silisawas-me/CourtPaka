using System.Security.Claims;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Observability;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// Paying for a hold: the booker transfers the money themselves and sends a picture of the
/// transfer, which moves the booking into the venue's checking queue (PRD US-04). Checking it is
/// US-12; nothing here decides whether the money arrived.
/// </summary>
public static class SlipEndpoints
{
    public static void MapSlipEndpoints(this RouteGroupBuilder bookings)
    {
        bookings.MapPost("/{bookingId:guid}/slip", UploadAsync)
            .RequireRateLimiting(RateLimitPolicies.Upload)
            .DisableAntiforgery();

        bookings.MapGet("/{bookingId:guid}/slip", DownloadAsync);
    }

    /// <summary>
    /// Takes the slip and moves the booking to <see cref="BookingStatus.PendingVerification"/>.
    /// A booker may send a better picture while it is waiting; the earlier ones are kept, because
    /// what the venue was shown at each point is part of the booking's record (PRD US-04).
    /// </summary>
    private static async Task<Results<Ok<BookingResponse>, ProblemHttpResult>> UploadAsync(
        Guid bookingId,
        IFormFile? file,
        ClaimsPrincipal principal,
        AppDbContext database,
        ISlipStore slips,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var bookerId = CallerId.Of(principal);

        if (file is null || file.Length == 0)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.NoFile);
        }

        if (file.Length > PaymentSlip.MaxBytes)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.TooLarge);
        }

        var booking = await database.Bookings
            .SingleOrDefaultAsync(
                candidate => candidate.Id == bookingId && candidate.BookerUserId == bookerId,
                cancellationToken);

        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        // The server's clock decides, not the page's countdown (PRD US-04). A booker who has
        // already transferred is told to talk to the venue rather than left with nothing to do.
        // Either the hold has quietly run out, or something has already written that down — a
        // booker whose hours were taken by someone else meets the second. Both need the answer
        // that tells them what to do about money they may already have sent.
        if (BookedSlots.HasLapsed(booking, now) || booking.Status == BookingStatus.Expired)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, SlipErrorCodes.HoldExpired);
        }

        if (booking.Status is not (BookingStatus.Held or BookingStatus.PendingVerification))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, SlipErrorCodes.NotAwaitingPayment);
        }

        await using var content = file.OpenReadStream();
        var start = new byte[SlipValidation.SniffBytes];
        var read = await content.ReadAtLeastAsync(
            start, start.Length, throwOnEndOfStream: false, cancellationToken);

        if (SlipValidation.ContentTypeOf(start.AsSpan(0, read)) is not { } contentType)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.UnsupportedFile);
        }

        // The form is buffered before the handler runs, so reading the first bytes costs the
        // rewind and nothing more.
        content.Position = 0;
        var stored = await slips.SaveAsync(content, contentType, cancellationToken);

        // The same picture sent for two bookings at one venue is worth a second look by the venue,
        // and the booker is deliberately not told (PRD BR-07).
        var sameBytes = await database.PaymentSlips
            .Where(slip =>
                slip.Sha256 == stored.Sha256
                && slip.Booking!.VenueId == booking.VenueId
                // Re-sending the same picture for the same booking is a booker correcting
                // themselves, not a slip used twice.
                && slip.BookingId != booking.Id)
            .OrderBy(slip => slip.UploadedAt)
            .Select(slip => (Guid?)slip.Id)
            .FirstOrDefaultAsync(cancellationToken);

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        // The hold was read a moment ago, and a hold can run out between reading it and writing.
        // PRD 6.1 asks for the condition and the move to be decided together, so the move carries
        // the condition: it changes nothing unless the booking is still held and still in time.
        var moved = await database.Bookings
            .Where(candidate =>
                candidate.Id == booking.Id
                && candidate.Status == BookingStatus.Held
                && candidate.HoldExpiresAt > now)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    candidate => candidate.Status, BookingStatus.PendingVerification),
                cancellationToken);

        // Nothing moved: either this is a better picture for a booking already in the queue, or
        // the hold is gone. Asking the row settles it, rather than trusting the earlier read —
        // two uploads at once would otherwise both believe they were the one that moved it.
        var replacing = moved == 0
            && await database.Bookings.AnyAsync(
                candidate =>
                    candidate.Id == booking.Id
                    && candidate.Status == BookingStatus.PendingVerification,
                cancellationToken);

        if (moved == 0 && !replacing)
        {
            await transaction.RollbackAsync(cancellationToken);
            await slips.DeleteAsync(stored.Name, CancellationToken.None);
            return ApiProblem.Of(StatusCodes.Status409Conflict, SlipErrorCodes.HoldExpired);
        }

        // The move was made by a conditional update rather than the entity, so its record is
        // written here — in the same transaction, which is what keeps the two from disagreeing.
        if (moved == 1)
        {
            database.BookingStatusChanges.Add(BookingTransitions.Record(
                booking.Id,
                BookingStatus.Held,
                BookingStatus.PendingVerification,
                bookerId,
                now));
        }

        database.PaymentSlips.Add(new PaymentSlip
        {
            BookingId = booking.Id,
            UploadedByUserId = bookerId,
            UploadedAt = now,
            StoredName = stored.Name,
            ContentType = contentType,
            ByteSize = stored.ByteSize,
            Sha256 = stored.Sha256,
            SameBytesAsSlipId = sameBytes,
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // The bytes are on the volume but nothing will ever reference them, and that volume is
            // in the backup set (deploy/README.md). Take them back out.
            await slips.DeleteAsync(stored.Name, CancellationToken.None);
            throw;
        }

        // The name is part of the template, not a parameter: a sink that groups by template has
        // to see these as different events, which is the whole point of recording them (PRD 8).
        var events = AppEvents.For(loggers);
        if (moved == 0)
        {
            events.LogInformation(
                "slip_replaced {BookingId} {VenueId} {Bytes} {SameBytes}",
                booking.Id, booking.VenueId, stored.ByteSize, sameBytes is not null);
        }
        else
        {
            events.LogInformation(
                "slip_uploaded {BookingId} {VenueId} {Bytes} {SameBytes}",
                booking.Id, booking.VenueId, stored.ByteSize, sameBytes is not null);
            events.LogInformation(
                "booking_pending_verification {BookingId} {VenueId}", booking.Id, booking.VenueId);
        }

        return TypedResults.Ok(
            await BookingEndpoints.ReadBookingAsync(database, booking.Id, now, cancellationToken));
    }

    /// <summary>
    /// The slip itself. Only the booker who sent it, for now: the venue's own view of it is US-12,
    /// and an Admin reading one during a complaint is US-22 (PRD 8, PDPA).
    /// </summary>
    private static async Task<Results<FileStreamHttpResult, NotFound, ProblemHttpResult>> DownloadAsync(
        Guid bookingId,
        ClaimsPrincipal principal,
        HttpResponse response,
        AppDbContext database,
        ISlipStore slips,
        CancellationToken cancellationToken)
    {
        var bookerId = CallerId.Of(principal);

        var slip = await database.PaymentSlips
            .AsNoTracking()
            .Where(candidate =>
                candidate.BookingId == bookingId && candidate.Booking!.BookerUserId == bookerId)
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

        // Never rendered in the page's own origin. A PDF is a program as much as a document, and
        // this one came from whoever is holding the booking; handing it to the browser as a
        // download rather than a view means nothing in it runs next to the reader's session.
        // nosniff is set here as well as at the proxy, because the API is reachable without one.
        response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.File(
            content, slip.ContentType, fileDownloadName: slip.StoredName);
    }
}
