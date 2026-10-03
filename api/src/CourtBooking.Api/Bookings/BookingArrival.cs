using CourtBooking.Api.Identity;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// Whether the person who booked is going to turn up, and then whether they did (PRD US-24).
/// Stored, so the numbers must not move.
///
/// It is not the booking's status. A booking is paid for or it is not; this is about a person
/// walking through the door, and the counter needs both at once: a confirmed booking whose booker
/// has gone quiet is the one worth a phone call.
/// </summary>
public enum BookingArrival
{
    /// <summary>Nobody has said anything yet. Where every booking starts.</summary>
    Unconfirmed = 1,

    /// <summary>The booker has been written to about it (Jobs/BookerMail's two-hour notice).</summary>
    Reminded = 2,

    /// <summary>They said they are coming, or the venue asked them and wrote it down.</summary>
    Confirmed = 3,

    /// <summary>They are here.</summary>
    Arrived = 4,
}

/// <summary>
/// One step of that (PRD US-24, PRD 8's audit). Rows are only added — the database refuses
/// anything else — because "we called and they said yes" is the kind of thing that gets disputed
/// when somebody is charged for not turning up.
/// </summary>
public sealed class BookingArrivalChange
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid BookingId { get; init; }

    public required BookingArrival From { get; init; }

    public required BookingArrival To { get; init; }

    public required DateTimeOffset ChangedAt { get; init; }

    /// <summary>Who wrote it down, or null when the system did (a reminder going out).</summary>
    public Guid? ChangedByUserId { get; init; }

    public Booking? Booking { get; init; }

    public AppUser? ChangedBy { get; init; }
}

/// <summary>
/// The one way an arrival moves: forwards, and never past the end of the booking it belongs to.
///
/// Forwards only, because each step is something that happened — a message sent, a person on the
/// phone, a person at the desk — and none of them unhappens. A venue that marked the wrong
/// booking cancels or corrects the booking itself; this list stays as it was.
/// </summary>
public static class ArrivalTransitions
{
    /// <summary>
    /// How early somebody may be checked in. Early enough for the person who turns up while the
    /// court before theirs is finishing, short enough that it cannot be tomorrow's booking.
    /// </summary>
    public static readonly TimeSpan CheckInOpensBefore = TimeSpan.FromMinutes(30);

    /// <summary>Whether this is a step forward, which is the only kind there is.</summary>
    public static bool Allowed(BookingArrival from, BookingArrival to) => to > from;

    /// <summary>
    /// Moves the booking and writes the row that says so, or answers false when the move is not a
    /// step forward. Both happen in the caller's save, so the record and the state cannot part.
    /// </summary>
    public static bool Record(
        Booking booking,
        BookingArrival to,
        DateTimeOffset now,
        Guid? byUserId,
        out BookingArrivalChange? change)
    {
        change = null;
        if (!Allowed(booking.Arrival, to))
        {
            return false;
        }

        change = new BookingArrivalChange
        {
            BookingId = booking.Id,
            From = booking.Arrival,
            To = to,
            ChangedAt = now,
            ChangedByUserId = byUserId,
        };

        booking.Arrival = to;
        if (to == BookingArrival.Arrived)
        {
            booking.ArrivedAt = now;
        }

        return true;
    }
}
