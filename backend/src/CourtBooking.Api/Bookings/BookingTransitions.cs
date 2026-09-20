using CourtBooking.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// Which moves through the booking's life are allowed, and the one way to make one (PRD 6.1).
///
/// The table is transcribed from the state machine rather than spread across the handlers that
/// happen to need a move: US-12 and US-13 add a dozen of them, each with an actor, a reason and a
/// refund, and a move nobody wrote down is how a booking ends up somewhere the rules do not cover.
///
/// Every move is recorded (PRD 6.1 asks for who, when and why), and the record is written by
/// whatever makes the move — never beside it, where the two could disagree.
///
/// Only the moves that exist so far are listed. Adding a story means adding its rows, not
/// repeating an assignment.
/// </summary>
public static class BookingTransitions
{
    private static readonly Dictionary<BookingStatus, BookingStatus[]> Allowed = new()
    {
        // The booker sent a slip, or let the hold run out (PRD US-04, BR-02).
        [BookingStatus.Held] = [BookingStatus.PendingVerification, BookingStatus.Expired],

        // The booker may send a better picture while the venue is still looking (PRD US-04).
        [BookingStatus.PendingVerification] = [BookingStatus.PendingVerification],
    };

    /// <summary>
    /// Checks a move against the table and builds its record. Writing the record and the status
    /// together is the caller's to do, in one transaction — which is the only way the two paths
    /// that exist can do it, since both change the row rather than the entity.
    ///
    /// When an entity-level move arrives (US-12, US-13), the thin wrapper that sets the status and
    /// appends the record belongs here, over this.
    /// </summary>
    public static BookingStatusChange Record(
        Guid bookingId,
        BookingStatus? from,
        BookingStatus to,
        Guid? byUserId,
        DateTimeOffset at,
        string? reason = null)
    {
        if (from is { } current && !CanMove(current, to))
        {
            throw new InvalidOperationException(
                $"A booking cannot go from {current} to {to} (PRD 6.1).");
        }

        return new BookingStatusChange
        {
            BookingId = bookingId,
            From = from,
            To = to,
            ChangedAt = at,
            ChangedByUserId = byUserId,
            Reason = reason,
        };
    }

    /// <summary>
    /// Moves every booking a query selects, and records each one, in a single transaction. The
    /// bookings that moved are answered back so the caller can say so (PRD 8).
    ///
    /// The ids are read first and the update is made against those ids, so the rows recorded and
    /// the rows changed are the same rows — asking the question twice would let them differ under
    /// concurrency, which is the one thing an audit trail may not do.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> MoveAllAsync(
        AppDbContext database,
        IQueryable<Booking> bookings,
        BookingStatus from,
        BookingStatus to,
        Guid? byUserId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var moving = await bookings
            .Where(booking => booking.Status == from)
            .Select(booking => booking.Id)
            .ToListAsync(cancellationToken);

        if (moving.Count == 0)
        {
            return [];
        }

        // One transaction, unless the caller already opened one, in which case this joins it.
        var own = database.Database.CurrentTransaction is null
            ? await database.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            await database.Bookings
                .Where(booking => moving.Contains(booking.Id) && booking.Status == from)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(booking => booking.Status, to),
                    cancellationToken);

            await database.BookingStatusChanges.AddRangeAsync(
                moving.Select(id => Record(id, from, to, byUserId, at)), cancellationToken);
            await database.SaveChangesAsync(cancellationToken);

            if (own is not null)
            {
                await own.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync();
            }
        }

        return moving;
    }

    public static bool CanMove(BookingStatus from, BookingStatus to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    /// <summary>
    /// The event name for a booking that has just arrived somewhere, as PRD 8 names them:
    /// <c>booking_pending_verification</c>, <c>booking_expired</c>, and so on.
    /// </summary>
    public static string EventName(BookingStatus status) =>
        $"booking_{string.Concat(status.ToString().Select((letter, index) =>
            char.IsUpper(letter) && index > 0 ? $"_{char.ToLowerInvariant(letter)}" : $"{char.ToLowerInvariant(letter)}"))}";
}
