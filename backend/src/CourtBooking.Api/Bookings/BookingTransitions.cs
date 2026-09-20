using CourtBooking.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

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
        // The booker sent a slip, let the hold run out, or let it go (PRD US-04, US-05, BR-02).
        [BookingStatus.Held] =
        [
            BookingStatus.PendingVerification,
            BookingStatus.Expired,
            BookingStatus.Cancelled,
        ],

        // The venue checks the slip: the money is there, or it is not (PRD US-12). A better
        // picture from the booker lands here too, which is the move to itself.
        [BookingStatus.PendingVerification] =
        [
            BookingStatus.PendingVerification,
            BookingStatus.Confirmed,
            BookingStatus.Rejected,

            // The booker gave the hours up while the venue was still looking (PRD US-05).
            BookingStatus.Cancelled,
        ],

        // The hours were played, given up before they were, or nobody turned up for them
        // (PRD 6.1, US-05, US-13). Nobody presses Completed; the clock does.
        [BookingStatus.Confirmed] =
        [
            BookingStatus.Completed,
            BookingStatus.Cancelled,
            BookingStatus.NoShow,
        ],

        // For a day after the hours, the venue may correct what it recorded about them (US-13).
        [BookingStatus.Completed] = [BookingStatus.NoShow, BookingStatus.Cancelled],

        // Somebody did turn up after all, and the no-show was a mistake (US-13).
        [BookingStatus.NoShow] = [BookingStatus.Completed],
    };

    /// <summary>
    /// The moves the state machine will not make without being told why (PRD 6.1). A reason is
    /// part of the record, so a move that needs one and has none is refused before anything moves.
    /// </summary>
    private static readonly HashSet<(BookingStatus From, BookingStatus To)> NeedReason =
    [
        (BookingStatus.PendingVerification, BookingStatus.Rejected),

        // Undoing a no-show says that what is written down is wrong, and a record that says so
        // without saying why is worth nothing to whoever reads it later (PRD 6.1).
        (BookingStatus.NoShow, BookingStatus.Completed),
    ];

    // The venue needs a reason to cancel a booking that has been paid for, and the booker does
    // not (PRD 6.1). That is a rule about who is asking rather than about the move, so it lives
    // with the venue's rules in VenueDecisions and not in the table above.

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
        BookingStatus from,
        BookingStatus to,
        Guid? byUserId,
        DateTimeOffset at,
        string? reason = null)
    {
        if (!CanMove(from, to))
        {
            throw new InvalidOperationException(
                $"A booking cannot go from {from} to {to} (PRD 6.1).");
        }

        if (NeedReason.Contains((from, to)) && string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException(
                $"Going from {from} to {to} has to say why (PRD 6.1).");
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
    /// The first row of a booking's history: it coming into existence. Its own door, because a
    /// move with no "from" cannot be checked against the table, and a caller that wants one
    /// should have to say that is what it means.
    /// </summary>
    public static BookingStatusChange Created(
        Guid bookingId,
        BookingStatus status,
        Guid byUserId,
        DateTimeOffset at) =>
        new()
        {
            BookingId = bookingId,
            From = null,
            To = status,
            ChangedAt = at,
            ChangedByUserId = byUserId,
        };

    /// <summary>
    /// Moves every booking a query selects, and records each one, in a single transaction. The
    /// bookings that moved are answered back so the caller can say so (PRD 8).
    ///
    /// The update hands back the rows it actually changed, and those are the rows that get a
    /// record. Asking which rows to change and then assuming they all changed is how an audit
    /// trail ends up holding a move that never happened: two requests reaching the same lapsed
    /// hold both see it as Held, one of them changes nothing, and without RETURNING both would
    /// write it down.
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
        // Before anything is written. The record builder checks too, but by then the rows have
        // already changed — a guard that fires after the fact is a rollback, not a guard.
        if (!CanMove(from, to))
        {
            throw new InvalidOperationException(
                $"A booking cannot go from {from} to {to} (PRD 6.1).");
        }

        var candidates = await bookings
            .Where(booking => booking.Status == from)
            .Select(booking => booking.Id)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return [];
        }

        // One transaction, unless the caller already opened one, in which case this joins it.
        var own = database.Database.CurrentTransaction is null
            ? await database.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            var moved = await ChangeStatusAsync(database, candidates, from, to, cancellationToken);
            if (moved.Count > 0)
            {
                await database.BookingStatusChanges.AddRangeAsync(
                    moved.Select(id => Record(id, from, to, byUserId, at)), cancellationToken);
                await database.SaveChangesAsync(cancellationToken);
            }

            if (own is not null)
            {
                await own.CommitAsync(cancellationToken);
            }

            return moved;
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The status change itself, answering which rows it changed. Raw SQL because RETURNING is the
    /// whole point and no LINQ form of it exists: <c>ExecuteUpdate</c> answers a count, and a count
    /// cannot say which.
    /// </summary>
    private static async Task<List<Guid>> ChangeStatusAsync(
        AppDbContext database,
        List<Guid> candidates,
        BookingStatus from,
        BookingStatus to,
        CancellationToken cancellationToken)
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            """
            UPDATE "Bookings" SET "Status" = @to
            WHERE "Id" = ANY(@ids) AND "Status" = @from
            RETURNING "Id"
            """;
        command.Parameters.Add(new NpgsqlParameter("to", (int)to));
        command.Parameters.Add(new NpgsqlParameter("from", (int)from));
        command.Parameters.Add(new NpgsqlParameter("ids", candidates.ToArray()));

        var moved = new List<Guid>(candidates.Count);
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        while (await rows.ReadAsync(cancellationToken))
        {
            moved.Add(rows.GetGuid(0));
        }

        return moved;
    }

    private static bool CanMove(BookingStatus from, BookingStatus to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    /// <summary>
    /// The event name for a booking that has just arrived somewhere, as PRD 8 names them:
    /// <c>booking_pending_verification</c>, <c>booking_expired</c>, and so on.
    /// </summary>
    public static string EventName(BookingStatus status) =>
        $"booking_{string.Concat(status.ToString().Select((letter, index) =>
            char.IsUpper(letter) && index > 0 ? $"_{char.ToLowerInvariant(letter)}" : $"{char.ToLowerInvariant(letter)}"))}";
}
