namespace CourtBooking.Api.Bookings;

/// <summary>
/// Which moves through the booking's life are allowed, and the one way to make one (PRD 6.1).
///
/// The table is transcribed from the state machine rather than spread across the handlers that
/// happen to need a move: US-12 and US-13 add a dozen of them, each with an actor, a reason and a
/// refund, and a move nobody wrote down is how a booking ends up somewhere the rules do not cover.
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
    /// Moves the booking and records the move, or refuses to. A move that is not in the table is a
    /// bug in the caller, not something to explain to whoever is holding the booking.
    ///
    /// The history row is added to the booking rather than written here, so it is saved in the
    /// same SaveChanges as the status it describes — the two cannot end up disagreeing.
    /// </summary>
    public static void MoveTo(
        this Booking booking,
        BookingStatus to,
        Guid? byUserId,
        DateTimeOffset at,
        string? reason = null)
    {
        if (!CanMove(booking.Status, to))
        {
            throw new InvalidOperationException(
                $"A booking cannot go from {booking.Status} to {to} (PRD 6.1).");
        }

        booking.StatusChanges.Add(new BookingStatusChange
        {
            BookingId = booking.Id,
            From = booking.Status,
            To = to,
            ChangedAt = at,
            ChangedByUserId = byUserId,
            Reason = reason,
        });

        booking.Status = to;
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
