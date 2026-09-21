using System.Security.Claims;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Observability;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>The booking is named as typed: an admin pastes it from a message, and a mistyped one is
/// answered as "no such booking", not as a request the server could not read.</summary>
public sealed record OpenComplaintRequest(string? BookingId, string? Details, string? Channel);

public sealed record ResolveComplaintRequest(string? Resolution);

public sealed record ComplaintSummaryResponse(
    Guid Id,
    Guid BookingId,
    string VenueName,
    string Channel,
    string Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ResolvedAt);

public sealed record ComplaintSlotResponse(string CourtName, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record ComplaintMoveResponse(
    string? From,
    string To,
    DateTimeOffset ChangedAt,
    string? ChangedByEmail,
    string? Cause,
    string? Reason);

/// <summary>The booking a complaint is about, as much as an admin needs to judge it (PRD US-22).</summary>
public sealed record ComplaintBookingResponse(
    Guid BookingId,
    Guid VenueId,
    string VenueCode,
    string VenueName,
    string Channel,
    string? BookerEmail,
    string? CustomerName,
    string? CustomerPhone,
    string Status,
    string PaymentState,
    decimal TotalBaht,
    decimal RefundDueBaht,
    decimal SentBackBaht,
    ComplaintSlotResponse[] Slots,
    ComplaintMoveResponse[] History,
    bool HasSlip);

public sealed record SlipViewingResponse(string? ViewedByEmail, DateTimeOffset ViewedAt);

public sealed record ComplaintResponse(
    Guid Id,
    string Details,
    string Channel,
    string Status,
    DateTimeOffset OpenedAt,
    string? OpenedByEmail,
    DateTimeOffset? ResolvedAt,
    string? ResolvedByEmail,
    string? Resolution,
    ComplaintBookingResponse Booking,
    SlipViewingResponse[] SlipViewings);

public static class ComplaintErrorCodes
{
    public const string BookingNotFound = "complaint.booking_not_found";
    public const string DetailsRequired = "complaint.details_required";
    public const string DetailsTooLong = "complaint.details_too_long";
    public const string InvalidChannel = "complaint.invalid_channel";
    public const string ResolutionRequired = "complaint.resolution_required";
    public const string ResolutionTooLong = "complaint.resolution_too_long";
    public const string AlreadyResolved = "complaint.already_resolved";
    public const string NotOpen = "complaint.not_open";
    public const string InvalidStatus = "complaint.invalid_status";
}

/// <summary>
/// Complaints the platform takes about bookings (PRD US-22).
///
/// This is also the one way the platform sees a booker's slip, and only through a complaint that
/// is still open: the slip is a bank account (PDPA, PRD 8), and a reason to look ends when the
/// complaint does. Every look is recorded before the file is handed over.
/// </summary>
public static class ComplaintEndpoints
{
    public static void MapComplaintEndpoints(this IEndpointRouteBuilder routes)
    {
        var admin = routes.MapGroup("/admin/complaints")
            .WithTags("Platform")
            .RequireAuthorization(PlatformAdmins.PolicyName);

        admin.MapGet("/", ListAsync);
        admin.MapPost("/", OpenAsync);
        admin.MapGet("/{complaintId:guid}", OneAsync);
        admin.MapPost("/{complaintId:guid}/resolve", ResolveAsync);
        admin.MapGet("/{complaintId:guid}/slip", SlipAsync);
    }

    /// <summary>Open ones first, newest first: the queue is worked from the top.</summary>
    private static async Task<Results<Ok<ComplaintSummaryResponse[]>, ProblemHttpResult>> ListAsync(
        string? status,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        ComplaintStatus? wanted = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.GetNames<ComplaintStatus>().Contains(status, StringComparer.Ordinal))
            {
                return ApiProblem.Of(StatusCodes.Status400BadRequest, ComplaintErrorCodes.InvalidStatus);
            }

            wanted = Enum.Parse<ComplaintStatus>(status);
        }

        var complaints = await database.Complaints
            .AsNoTracking()
            .Where(complaint => wanted == null || complaint.Status == wanted)
            .OrderBy(complaint => complaint.Status)
            .ThenByDescending(complaint => complaint.OpenedAt)
            .Select(complaint => new ComplaintSummaryResponse(
                complaint.Id,
                complaint.BookingId,
                complaint.Booking!.Venue!.Name,
                complaint.Channel.ToString(),
                complaint.Status.ToString(),
                complaint.OpenedAt,
                complaint.ResolvedAt))
            .ToArrayAsync(cancellationToken);

        return TypedResults.Ok(complaints);
    }

    private static async Task<Results<Created<ComplaintResponse>, ProblemHttpResult>> OpenAsync(
        OpenComplaintRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var details = request.Details?.Trim();
        if (string.IsNullOrEmpty(details))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, ComplaintErrorCodes.DetailsRequired);
        }

        if (details.Length > Complaint.DetailsMaxLength)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, ComplaintErrorCodes.DetailsTooLong);
        }

        // The names and nothing else: Enum.TryParse would take "1" and "Email,Phone".
        if (request.Channel is not { } channel
            || !Enum.GetNames<ComplaintChannel>().Contains(channel, StringComparer.Ordinal))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, ComplaintErrorCodes.InvalidChannel);
        }

        if (!Guid.TryParse(request.BookingId?.Trim(), out var bookingId)
            || !await database.Bookings.AnyAsync(booking => booking.Id == bookingId, cancellationToken))
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, ComplaintErrorCodes.BookingNotFound);
        }

        var complaint = new Complaint
        {
            BookingId = bookingId,
            Details = details,
            Channel = Enum.Parse<ComplaintChannel>(channel),
            OpenedAt = timeProvider.GetUtcNow(),
            OpenedByUserId = CallerId.Of(principal),
        };
        database.Complaints.Add(complaint);
        await database.SaveChangesAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "complaint_opened {ComplaintId} {BookingId} {Channel}",
            complaint.Id, bookingId, complaint.Channel);

        return TypedResults.Created(
            $"/api/admin/complaints/{complaint.Id}",
            await DrawnAsync(database, complaint.Id, timeProvider.GetUtcNow(), cancellationToken));
    }

    private static async Task<Results<Ok<ComplaintResponse>, NotFound>> OneAsync(
        Guid complaintId,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await database.Complaints.AnyAsync(complaint => complaint.Id == complaintId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(
            await DrawnAsync(database, complaintId, timeProvider.GetUtcNow(), cancellationToken));
    }

    /// <summary>
    /// Closes a complaint with what was done about it. A conditional write, so two admins
    /// resolving at once cannot both record an answer.
    /// </summary>
    private static async Task<Results<Ok<ComplaintResponse>, ProblemHttpResult, NotFound>> ResolveAsync(
        Guid complaintId,
        ResolveComplaintRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var resolution = request.Resolution?.Trim();
        if (string.IsNullOrEmpty(resolution))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, ComplaintErrorCodes.ResolutionRequired);
        }

        if (resolution.Length > Complaint.ResolutionMaxLength)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, ComplaintErrorCodes.ResolutionTooLong);
        }

        var now = timeProvider.GetUtcNow();
        var byUserId = CallerId.Of(principal);

        var resolved = await database.Complaints
            .Where(complaint => complaint.Id == complaintId && complaint.Status == ComplaintStatus.Open)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(complaint => complaint.Status, ComplaintStatus.Resolved)
                    .SetProperty(complaint => complaint.ResolvedAt, now)
                    .SetProperty(complaint => complaint.ResolvedByUserId, byUserId)
                    .SetProperty(complaint => complaint.Resolution, resolution),
                cancellationToken);

        if (resolved == 0)
        {
            return await database.Complaints.AnyAsync(complaint => complaint.Id == complaintId, cancellationToken)
                ? ApiProblem.Of(StatusCodes.Status409Conflict, ComplaintErrorCodes.AlreadyResolved)
                : TypedResults.NotFound();
        }

        AppEvents.For(loggers).LogInformation(
            "complaint_resolved {ComplaintId} {By}", complaintId, byUserId);

        return TypedResults.Ok(await DrawnAsync(database, complaintId, now, cancellationToken));
    }

    /// <summary>
    /// The booking's newest slip, for an admin working an open complaint about it (PRD US-22).
    /// The look is written down and committed first; only then is the file opened.
    /// </summary>
    private static async Task<Results<FileStreamHttpResult, NotFound, ProblemHttpResult>> SlipAsync(
        Guid complaintId,
        ClaimsPrincipal principal,
        HttpResponse response,
        AppDbContext database,
        ISlipStore slips,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var complaint = await database.Complaints
            .AsNoTracking()
            .Where(one => one.Id == complaintId)
            .Select(one => new { one.BookingId, one.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (complaint is null)
        {
            return TypedResults.NotFound();
        }

        if (complaint.Status != ComplaintStatus.Open)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, ComplaintErrorCodes.NotOpen);
        }

        var readable = database.PaymentSlips.Where(slip => slip.BookingId == complaint.BookingId);
        var newest = await readable.AsNoTracking().NewestFirst()
            .Select(slip => (Guid?)slip.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (newest is not { } slipId)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, SlipErrorCodes.NoSlip);
        }

        var byUserId = CallerId.Of(principal);

        // The complaint is held open while the look is written: a resolve arriving now waits for
        // this commit (its UPDATE needs the row this share-locks), and one that got there first
        // leaves nothing to lock — so no look is ever recorded on a closed complaint, and no slip
        // is handed over through one (PRD 8).
        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            var held = await database.Database
                .SqlQuery<Guid>(
                    $"""
                    SELECT "Id" AS "Value" FROM "Complaints"
                    WHERE "Id" = {complaintId} AND "Status" = {(int)ComplaintStatus.Open}
                    FOR SHARE
                    """)
                .ToListAsync(cancellationToken);

            if (held.Count == 0)
            {
                return ApiProblem.Of(StatusCodes.Status409Conflict, ComplaintErrorCodes.NotOpen);
            }

            database.SlipViewings.Add(new SlipViewing
            {
                ComplaintId = complaintId,
                SlipId = slipId,
                ViewedByUserId = byUserId,
                ViewedAt = timeProvider.GetUtcNow(),
            });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        AppEvents.For(loggers).LogInformation(
            "complaint_slip_viewed {ComplaintId} {SlipId} {By}", complaintId, slipId, byUserId);

        // The same slip that was recorded, not "the newest" asked again: one sent in between
        // must not be handed over under another's record.
        return await SlipDownload.NewestAsync(
            readable.Where(slip => slip.Id == slipId), slips, response, cancellationToken);
    }

    private static async Task<ComplaintResponse> DrawnAsync(
        AppDbContext database,
        Guid complaintId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var complaint = await database.Complaints
            .AsNoTracking()
            .Where(one => one.Id == complaintId)
            .Select(one => new
            {
                one.Id,
                one.BookingId,
                one.Details,
                one.Channel,
                one.Status,
                one.OpenedAt,
                OpenedByEmail = one.OpenedBy!.Email,
                one.ResolvedAt,
                ResolvedByEmail = one.ResolvedBy!.Email,
                one.Resolution,
            })
            .SingleAsync(cancellationToken);

        var booking = await database.Bookings
            .AsNoTracking()
            .Include(one => one.Slots).ThenInclude(slot => slot.Court)
            .Include(one => one.Venue)
            .Include(one => one.Booker)
            .SingleAsync(one => one.Id == complaint.BookingId, cancellationToken);

        var moves = await database.BookingStatusChanges
            .AsNoTracking()
            .Where(change => change.BookingId == booking.Id)
            .Select(change => new
            {
                change.From,
                change.To,
                change.ChangedAt,
                ChangedByEmail = change.ChangedBy!.Email,
                change.Cause,
                change.Reason,
            })
            .ToListAsync(cancellationToken);

        // In the order it happened, which the clock alone cannot say (see BookingHistory).
        var history = BookingHistory
            .InOrder(moves, move => move.From, move => move.To, move => move.ChangedAt)
            .Select(move => new ComplaintMoveResponse(
                move.From?.ToString(),
                move.To.ToString(),
                move.ChangedAt,
                move.ChangedByEmail,
                move.Cause?.ToString(),
                move.Reason))
            .ToArray();

        var sentBack = await database.RefundRecords
            .Where(record => record.BookingId == booking.Id)
            .StillStanding()
            .SumAsync(record => record.AmountBaht, cancellationToken);

        var hasSlip = await database.PaymentSlips.AnyAsync(
            slip => slip.BookingId == booking.Id, cancellationToken);

        var viewings = await database.SlipViewings
            .AsNoTracking()
            .Where(viewing => viewing.ComplaintId == complaintId)
            .OrderByDescending(viewing => viewing.ViewedAt)
            .Select(viewing => new SlipViewingResponse(viewing.ViewedBy!.Email, viewing.ViewedAt))
            .ToArrayAsync(cancellationToken);

        return new ComplaintResponse(
            complaint.Id,
            complaint.Details,
            complaint.Channel.ToString(),
            complaint.Status.ToString(),
            complaint.OpenedAt,
            complaint.OpenedByEmail,
            complaint.ResolvedAt,
            complaint.ResolvedByEmail,
            complaint.Resolution,
            new ComplaintBookingResponse(
                booking.Id,
                booking.VenueId,
                booking.Venue!.Code,
                booking.Venue.Name,
                booking.Channel.ToString(),
                booking.Booker is { DeletedAt: null } booker ? booker.Email : null,
                booking.CustomerName,
                booking.CustomerPhone,
                BookedSlots.StatusAt(booking, now).ToString(),
                booking.PaymentState.ToString(),
                booking.TotalBaht,
                booking.RefundDueBaht,
                sentBack,
                [.. booking.Slots
                    .OrderBy(slot => slot.StartsAt)
                    .Select(slot => new ComplaintSlotResponse(slot.Court!.Name, slot.StartsAt, slot.EndsAt))],
                history,
                hasSlip),
            viewings);
    }
}
