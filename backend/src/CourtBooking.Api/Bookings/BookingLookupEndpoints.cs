using CourtBooking.Api.Data;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// The booking list's two questions the day's list cannot answer (owner app, "รายการจอง"):
/// "which booking is this caller's", on any day, and "what has happened to this one".
///
/// Both sit inside the bookings group, so both need <c>ManageBookings</c> and stay open at a
/// suspended venue: they read what was sold, and name the people it was sold to (PDPA, US-13).
/// </summary>
public static class BookingLookupEndpoints
{
    /// <summary>Fewer letters than this match half the venue's customers.</summary>
    public const int MinQueryLength = 2;

    /// <summary>The most recent matches, which is the one a caller is asking about.</summary>
    public const int MaxResults = 50;

    public static void MapBookingLookupEndpoints(this RouteGroupBuilder bookings)
    {
        bookings.MapGet("/find", FindAsync);
        bookings.MapGet("/{bookingId:guid}/history", HistoryAsync);
    }

    /// <summary>
    /// Bookings whose customer's name or phone holds what was typed, whatever the day. A booker
    /// who deleted their account is matched by nothing of theirs (PDPA): only the booking stays.
    /// </summary>
    private static async Task<Ok<VenueBookingResponse[]>> FindAsync(
        Guid venueId,
        string? q,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var typed = q?.Trim() ?? "";
        if (typed.Length < MinQueryLength)
        {
            return TypedResults.Ok(Array.Empty<VenueBookingResponse>());
        }

        var term = $"%{PublicVenueEndpoints.Like(typed)}%";
        const string escape = PublicVenueEndpoints.LikeEscape;

        var ids = await database.Bookings
            .Where(booking => booking.VenueId == venueId
                && (EF.Functions.ILike(booking.CustomerName ?? "", term, escape)
                    || EF.Functions.ILike(booking.CustomerPhone ?? "", term, escape)
                    || (booking.Booker != null
                        && booking.Booker.DeletedAt == null
                        && (EF.Functions.ILike(booking.Booker.DisplayName ?? "", term, escape)
                            || EF.Functions.ILike(booking.Booker.Email ?? "", term, escape)
                            || EF.Functions.ILike(booking.Booker.PhoneNumber ?? "", term, escape)))))
            .OrderByDescending(booking => booking.Slots.Min(slot => slot.StartsAt))
            .Select(booking => booking.Id)
            .Take(MaxResults)
            .ToArrayAsync(cancellationToken);

        var found = await VenueBookingEndpoints.ReadManyAsync(
            database,
            database.Bookings.Where(booking => ids.Contains(booking.Id)),
            venueId,
            VenueBookingEndpoints.IsOwner(venue.Require()),
            timeProvider.GetUtcNow(),
            cancellationToken);

        return TypedResults.Ok(found);
    }

    /// <summary>
    /// Everything written down about one booking, oldest first: how it moved, who turned up,
    /// which hours changed, the money in and the money sent back. Read from the append-only
    /// tables, which are the record; nothing here is recomputed.
    /// </summary>
    private static async Task<Results<Ok<BookingHistoryEntryResponse[]>, NotFound>> HistoryAsync(
        Guid venueId,
        Guid bookingId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var exists = await database.Bookings
            .AnyAsync(booking => booking.Id == bookingId && booking.VenueId == venueId, cancellationToken);
        if (!exists)
        {
            return TypedResults.NotFound();
        }

        var statuses = await database.BookingStatusChanges
            .Where(change => change.BookingId == bookingId)
            .Select(change => new { change.ChangedAt, change.From, change.To, change.Cause, change.ChangedByUserId })
            .ToListAsync(cancellationToken);
        var arrivals = await database.BookingArrivalChanges
            .Where(change => change.BookingId == bookingId)
            .Select(change => new { change.ChangedAt, change.From, change.To, change.ChangedByUserId })
            .ToListAsync(cancellationToken);
        var hours = await database.BookingSlotChanges
            .Where(change => change.BookingId == bookingId)
            .Select(change => new { change.ChangedAt, change.What, change.FromCourtId, change.ToCourtId, change.ChangedByUserId })
            .ToListAsync(cancellationToken);
        var receipts = await database.PaymentReceipts
            .Where(receipt => receipt.BookingId == bookingId)
            .Select(receipt => new { receipt.ReceivedAt, receipt.AmountBaht, receipt.Method, receipt.ReceivedByUserId })
            .ToListAsync(cancellationToken);
        var refunds = await database.RefundRecords
            .Where(record => record.BookingId == bookingId)
            .Select(record => new { record.RecordedAt, record.AmountBaht, record.Method, record.RecordedByUserId, record.VoidedAt })
            .ToListAsync(cancellationToken);

        var courts = await database.Courts
            .Where(court => court.VenueId == venueId)
            .ToDictionaryAsync(court => court.Id, court => court.Name, cancellationToken);

        var people = statuses.Select(row => row.ChangedByUserId)
            .Concat(arrivals.Select(row => row.ChangedByUserId))
            .Concat(hours.Select(row => (Guid?)row.ChangedByUserId))
            .Concat(receipts.Select(row => row.ReceivedByUserId))
            .Concat(refunds.Select(row => (Guid?)row.RecordedByUserId))
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        // Whoever pressed the door, by the name the counter knows them by. Somebody who deleted
        // their account is nobody here, as everywhere else (PDPA).
        var names = await database.Users
            .Where(user => people.Contains(user.Id) && user.DeletedAt == null)
            .Select(user => new { user.Id, Name = user.DisplayName ?? user.Email })
            .ToDictionaryAsync(user => user.Id, user => user.Name, cancellationToken);
        string? By(Guid? id) => id is Guid known ? names.GetValueOrDefault(known) : null;
        string? Court(Guid? id) => id is Guid known ? courts.GetValueOrDefault(known) : null;

        var entries = new List<BookingHistoryEntryResponse>();
        entries.AddRange(statuses.Select(row => new BookingHistoryEntryResponse(
            row.ChangedAt, nameof(BookingHistoryKind.Status), row.From?.ToString(), row.To.ToString(),
            null, null, null, null, 0, row.Cause?.ToString(), By(row.ChangedByUserId))));
        entries.AddRange(arrivals.Select(row => new BookingHistoryEntryResponse(
            row.ChangedAt, nameof(BookingHistoryKind.Arrival), row.From.ToString(), row.To.ToString(),
            null, null, null, null, 0, null, By(row.ChangedByUserId))));
        // One press moves or adds several hours; the record has a row an hour, the story one line.
        entries.AddRange(hours
            .GroupBy(row => new { row.ChangedAt, row.What })
            .Select(group => new BookingHistoryEntryResponse(
                group.Key.ChangedAt, nameof(BookingHistoryKind.Hours), null, group.Key.What.ToString(),
                null, null, Court(group.First().FromCourtId), Court(group.First().ToCourtId),
                group.Count(), null, By(group.First().ChangedByUserId))));
        entries.AddRange(receipts.Select(row => new BookingHistoryEntryResponse(
            row.ReceivedAt, nameof(BookingHistoryKind.Payment), null, null,
            row.AmountBaht, row.Method.ToString(), null, null, 0, null, By(row.ReceivedByUserId))));
        entries.AddRange(refunds.Select(row => new BookingHistoryEntryResponse(
            row.RecordedAt, (row.VoidedAt is null ? BookingHistoryKind.Refund : BookingHistoryKind.RefundVoided).ToString(), null, null,
            row.AmountBaht, row.Method.ToString(), null, null, 0, null, By(row.RecordedByUserId))));

        // Two rows of one press share a moment (confirmed, then completed by the clock); the
        // status chain keeps them in the order they happened, which a sort on time alone loses.
        return TypedResults.Ok(entries
            .Select((entry, index) => (entry, index))
            .OrderBy(pair => pair.entry.At)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.entry)
            .ToArray());
    }
}

public enum BookingHistoryKind
{
    Status,
    Arrival,
    Hours,
    Payment,
    Refund,
    RefundVoided,
}

/// <summary>
/// One line of a booking's story. Names and values only — the screen says it in the reader's
/// language (US-23). <c>From</c>/<c>To</c> are statuses, arrivals, or for hours the change made.
/// </summary>
public sealed record BookingHistoryEntryResponse(
    DateTimeOffset At,
    string Kind,
    string? From,
    string? To,
    decimal? AmountBaht,
    string? Method,
    string? FromCourt,
    string? ToCourt,
    int Hours,
    string? Cause,
    string? By);
