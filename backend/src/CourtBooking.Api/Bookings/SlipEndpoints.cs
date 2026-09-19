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
            .Include(candidate => candidate.Slots)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == bookingId && candidate.BookerUserId == bookerId,
                cancellationToken);

        if (booking is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        // The server's clock decides, not the page's countdown (PRD US-04). A booker who has
        // already transferred is told to talk to the venue rather than left with nothing to do.
        if (booking.Status == BookingStatus.Held && booking.HoldExpiresAt <= now)
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

        await using var whole = new MemoryStream(start, 0, read);
        var stored = await slips.SaveAsync(
            new ConcatenatedStream(whole, content), contentType, cancellationToken);

        // The same picture sent for two bookings at one venue is worth a second look by the venue,
        // and the booker is deliberately not told (PRD BR-07).
        var sameBytes = await database.PaymentSlips
            .Where(slip =>
                slip.Sha256 == stored.Sha256 && slip.Booking!.VenueId == booking.VenueId)
            .OrderBy(slip => slip.UploadedAt)
            .Select(slip => (Guid?)slip.Id)
            .FirstOrDefaultAsync(cancellationToken);

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

        booking.Status = BookingStatus.PendingVerification;
        await database.SaveChangesAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "slip_uploaded {BookingId} {VenueId} {Bytes} {SameBytes}",
            booking.Id,
            booking.VenueId,
            stored.ByteSize,
            sameBytes is not null);

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
        return content is null
            ? TypedResults.NotFound()
            : TypedResults.File(content, slip.ContentType);
    }
}

/// <summary>
/// Reads one stream then the next, so the bytes taken to recognise the file can be handed to the
/// store ahead of the rest without buffering the whole upload in memory.
/// </summary>
internal sealed class ConcatenatedStream(Stream first, Stream second) : Stream
{
    private bool firstDone;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (!firstDone)
        {
            var read = await first.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                return read;
            }
            firstDone = true;
        }

        return await second.ReadAsync(buffer, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
