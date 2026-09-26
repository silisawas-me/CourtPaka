using CourtBooking.Api.Identity;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Bookings;

/// <summary>What happened to an hour of a booking (PRD US-29).</summary>
public enum HoursChange
{
    /// <summary>An hour was added to the end of a booking that was still being played.</summary>
    Added = 1,

    /// <summary>An hour the booking already had was played on a different court.</summary>
    Moved = 2,
}

/// <summary>
/// One court-hour of a booking changing, and who changed it (PRD US-29, PRD 8). A row per hour: a
/// move of three hours says three times which court they left and which they went to, because the
/// question somebody asks afterwards is about one hour — the one they were standing on.
///
/// Rows are only ever added; the database refuses anything else. An hour that was sold twice, or a
/// game that was played somewhere other than where it was booked, is exactly the sort of thing
/// that is disputed later, and a record that can be edited is not an answer to a dispute.
/// </summary>
public sealed class BookingSlotChange
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid BookingId { get; init; }

    public required HoursChange What { get; init; }

    /// <summary>The court it was on. Null for an hour that was added: it was on none.</summary>
    public required Guid? FromCourtId { get; init; }

    /// <summary>The court it is on now.</summary>
    public required Guid ToCourtId { get; init; }

    /// <summary>The hour itself, which neither door changes — both keep the clock as it was.</summary>
    public required DateTimeOffset StartsAt { get; init; }

    /// <summary>
    /// What the hour cost. An added hour is priced when it is added (PRD US-29), so this is the
    /// only record of the price a booking's own snapshot does not carry; a moved hour keeps the
    /// price it was sold at, and this says so.
    /// </summary>
    public required decimal BahtPerHour { get; init; }

    public required DateTimeOffset ChangedAt { get; init; }

    /// <summary>Who did it. Never null: neither door is something a clock can do.</summary>
    public required Guid ChangedByUserId { get; init; }

    public Booking? Booking { get; init; }

    public Court? FromCourt { get; init; }

    public Court? ToCourt { get; init; }

    public AppUser? ChangedBy { get; init; }
}

/// <summary>
/// What a venue may still do to the hours a booking holds (PRD US-29): sell it one more, or put it
/// on a different court. The one place that answers either, so the buttons the counter is shown and
/// the doors the server opens cannot disagree.
///
/// Neither changes the booking's status, and neither is a new booking. An evening that runs on is
/// the same evening, and the money for the extra hour is owed on the booking that was already made.
/// </summary>
public static class BookingHours
{
    /// <summary>
    /// The bookings whose hours can still change: one the venue is going to honour, and one it is
    /// honouring now. Asked of the status as it reads (PRD 9.2), so a booking whose last hour has
    /// finished is already out — there is nothing to extend once everybody has gone home, and the
    /// answer to somebody arriving afterwards is a booking of their own (PRD US-13).
    ///
    /// A hold is not one of them. PRD 6.1 gives a hold two ways out, a slip or the clock, and
    /// adding to one would be selling an hour to somebody who has not paid for the first.
    /// </summary>
    public static bool CanChange(BookingStatus status) =>
        status is BookingStatus.PendingVerification or BookingStatus.Confirmed;

    /// <summary>
    /// The hour a booking would run on into: the one that begins where its last hour ends. Null
    /// where its hours cannot change, or where it holds none.
    ///
    /// Taken from the hours the booking still holds rather than from all of them, because a
    /// released hour is one somebody else may already be playing.
    /// </summary>
    public static DateTimeOffset? NextHour(
        Booking booking,
        BookingStatus status,
        DateTimeOffset now)
    {
        if (!CanChange(status))
        {
            return null;
        }

        var holding = Holding(booking);
        if (holding.Count == 0)
        {
            return null;
        }

        // Asked of the clock and not only of the status. A booking still waiting to be checked
        // keeps that status after its hours are played — only a confirmed one reads as completed
        // (PRD 9.2) — and an evening everybody has gone home from has nothing to run on into,
        // whichever of the two it is stored as.
        var last = holding.Max(slot => slot.EndsAt);
        return last > now ? last : null;
    }

    /// <summary>
    /// The court that hour would be on if nobody says otherwise: the one the booking's last hour
    /// is being played on (PRD US-29 — "the same court"). Where two courts end together, the
    /// lowest id, so asking twice gives the same answer.
    /// </summary>
    public static Guid? SameCourt(Booking booking, BookingStatus status, DateTimeOffset now)
    {
        if (NextHour(booking, status, now) is not { } next)
        {
            return null;
        }

        var last = Holding(booking).Where(slot => slot.EndsAt == next).ToList();

        // A group playing its last hour across two courts has no "the same court" to run on
        // into, and half of them would be sent home. Nothing is offered rather than half of it;
        // the venue sells the extra hour as a booking of its own (PRD US-13).
        return last.Count == 1 ? last[0].CourtId : null;
    }

    /// <summary>
    /// Whether the hours that could still move hold two courts at the same moment. One court
    /// cannot take both, so neither door has anything to offer such a booking (PRD US-29).
    /// </summary>
    public static bool OnTwoCourtsAtOnce(IReadOnlyList<BookingSlot> hours) =>
        hours.Select(slot => slot.StartsAt).Distinct().Count() != hours.Count;

    /// <summary>
    /// The hours of this booking that could still be played somewhere else: the ones that have not
    /// finished, including the one running now — the players are on the court, and a court that
    /// has flooded is a reason to move them mid-game (PRD US-29). An hour behind them was played
    /// where it was played, and nothing moves that.
    /// </summary>
    public static IReadOnlyList<BookingSlot> Movable(
        Booking booking,
        BookingStatus status,
        DateTimeOffset now) =>
        CanChange(status)
            ? [.. Holding(booking).Where(slot => slot.EndsAt > now).OrderBy(slot => slot.StartsAt)]
            : [];

    /// <summary>The hours the booking still occupies a court with (PRD BR-04).</summary>
    private static List<BookingSlot> Holding(Booking booking) =>
        [.. booking.Slots.Where(slot => slot.IsActive)];
}
