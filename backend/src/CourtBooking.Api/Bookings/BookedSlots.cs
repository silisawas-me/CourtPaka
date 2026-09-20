using CourtBooking.Api.Data;
using CourtBooking.Api.Localization;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// Which court-hours are spoken for. One rule, read by the grid a booker looks at and by the write
/// that takes an hour, so the two can never disagree about what "taken" means.
/// </summary>
public static class BookedSlots
{
    /// <summary>
    /// The statuses that still occupy a court (PRD BR-04). <c>NoShow</c> is deliberately absent:
    /// the hours a no-show did not turn up for go back on sale.
    /// </summary>
    public static readonly BookingStatus[] OccupyingStatuses =
    [
        BookingStatus.Held,
        BookingStatus.PendingVerification,
        BookingStatus.Confirmed,
        BookingStatus.Completed,
    ];

    /// <summary>
    /// Whether this hold's fifteen minutes are up. The one statement of the rule (PRD BR-02); the
    /// query below says the same thing in the form the database can read, and everything else here
    /// and in the endpoints is defined against one of the two.
    /// </summary>
    public static bool HasLapsed(Booking booking, DateTimeOffset now) =>
        booking.Status == BookingStatus.Held && booking.HoldExpiresAt <= now;

    /// <summary>Holds whose fifteen minutes are up, as a query.</summary>
    public static IQueryable<Booking> Lapsed(AppDbContext database, DateTimeOffset now) =>
        database.Bookings.Where(booking =>
            booking.Status == BookingStatus.Held && booking.HoldExpiresAt <= now);

    /// <summary>
    /// The one booking a booker may have waiting to be paid for (PRD S-22). A hold that has lapsed
    /// is not one of them, whether or not anything has marked it <c>Expired</c> yet.
    /// </summary>
    public static IQueryable<Booking> LiveHolds(AppDbContext database, DateTimeOffset now) =>
        database.Bookings
            .Where(booking => booking.Status == BookingStatus.Held)
            .Where(booking => !Lapsed(database, now).Any(over => over.Id == booking.Id));

    /// <summary>
    /// A lapsed hold does not hold anything, whether or not the job that marks it <c>Expired</c>
    /// has run (PRD 9.2). Every read and every write goes through this.
    /// </summary>
    public static IQueryable<BookingSlot> Active(AppDbContext database, DateTimeOffset now) =>
        database.BookingSlots
            .Where(slot => slot.IsActive)
            .Where(slot => OccupyingStatuses.Contains(slot.Booking!.Status))
            .Where(slot => !Lapsed(database, now).Any(over => over.Id == slot.BookingId));

    /// <summary>
    /// Lets go of the hours lapsed holds claim on these courts, and marks those holds
    /// <see cref="BookingStatus.Expired"/>.
    ///
    /// A read can tell a lapsed hold from a live one on its own; the database cannot, because the
    /// exclusion constraint only sees <see cref="BookingSlot.IsActive"/>. Without this, an hour the
    /// grid calls free stays unbookable for as long as that column says otherwise — which, with no
    /// expiry job yet, is forever. So the write releases them itself rather than waiting for a job
    /// (PRD 9.2).
    ///
    /// It is scoped to the courts being booked rather than sweeping the table: a booking only needs
    /// the hours it is asking for, and two bookings at unrelated venues should not be taking locks
    /// on each other's rows. Running it twice does nothing the first run did not.
    /// </summary>
    public static async Task ReleaseLapsedAsync(
        AppDbContext database,
        IReadOnlyCollection<Guid> courtIds,
        DateTimeOffset from,
        DateTimeOffset until,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (courtIds.Count == 0)
        {
            return;
        }

        var lapsed = Lapsed(database, now);

        var released = await database.BookingSlots
            .Where(slot =>
                slot.IsActive
                && courtIds.Contains(slot.CourtId)
                && slot.StartsAt >= from
                && slot.StartsAt < until
                && lapsed.Any(over => over.Id == slot.BookingId))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(slot => slot.IsActive, false), cancellationToken);

        if (released == 0)
        {
            return;
        }

        // Only the holds that just lost a slot, and only once there is something to say about them.
        await ExpireAsync(
            database,
            lapsed.Where(booking => !booking.Slots.Any(slot => slot.IsActive)),
            now,
            cancellationToken);
    }

    /// <summary>
    /// Ends this booker's own holds whose time is up, and lets go of the hours they claimed.
    ///
    /// The one-hold-per-booker index counts any row still marked <c>Held</c>, and it cannot ask
    /// what the time is, so a hold left to lapse would keep its booker locked out until someone
    /// happened to book those exact hours. This is what stops that (PRD S-22, BR-02).
    /// </summary>
    public static async Task ReleaseOwnLapsedAsync(
        AppDbContext database,
        Guid bookerUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var mine = Lapsed(database, now).Where(booking => booking.BookerUserId == bookerUserId);

        await database.BookingSlots
            .Where(slot => slot.IsActive && mine.Any(over => over.Id == slot.BookingId))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(slot => slot.IsActive, false), cancellationToken);

        await ExpireAsync(database, mine, now, cancellationToken);
    }

    /// <summary>
    /// Held → Expired, in bulk. It is a move the state machine allows (PRD 6.1), asserted here
    /// once because a set-based update cannot ask the entity.
    /// </summary>
    private static async Task ExpireAsync(
        AppDbContext database,
        IQueryable<Booking> lapsed,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!BookingTransitions.CanMove(BookingStatus.Held, BookingStatus.Expired))
        {
            throw new InvalidOperationException("Held may no longer expire; PRD 6.1 has changed.");
        }

        // The ids first, so each expiry gets its own record (PRD 6.1). The set is whatever lapsed
        // on the hours being asked for, which is nearly always nothing; the early return above
        // means this only runs when something did.
        var expiring = await lapsed.Select(booking => booking.Id).ToListAsync(cancellationToken);
        if (expiring.Count == 0)
        {
            return;
        }

        database.BookingStatusChanges.AddRange(expiring.Select(bookingId => new BookingStatusChange
        {
            BookingId = bookingId,
            From = BookingStatus.Held,
            To = BookingStatus.Expired,
            ChangedAt = now,
            // Nobody let it lapse; the clock did.
            ChangedByUserId = null,
        }));
        await database.SaveChangesAsync(cancellationToken);

        await lapsed.ExecuteUpdateAsync(
            setters => setters.SetProperty(booking => booking.Status, BookingStatus.Expired),
            cancellationToken);
    }

    /// <summary>
    /// The court-hours of one Bangkok day that a booker cannot take, as an hour per court. It asks
    /// by court rather than by venue so the query reads the (CourtId, StartsAt) index and never
    /// walks a venue's whole booking history to answer a question about one day.
    /// </summary>
    public static async Task<HashSet<(Guid CourtId, int Hour)>> OnAsync(
        AppDbContext database,
        IReadOnlyCollection<Guid> courtIds,
        DateOnly date,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (courtIds.Count == 0)
        {
            return [];
        }

        var from = PlatformRequirements.BangkokHour(date, 0);
        var until = PlatformRequirements.BangkokHour(date.AddDays(1), 0);

        var slots = await Active(database, now)
            .Where(slot =>
                courtIds.Contains(slot.CourtId) && slot.StartsAt >= from && slot.StartsAt < until)
            .Select(slot => new { slot.CourtId, slot.StartsAt })
            .ToListAsync(cancellationToken);

        // Every row is inside the day, so the hour is the distance from its start. Thailand has no
        // daylight saving, which is what makes that arithmetic and a timezone lookup agree.
        return slots
            .Select(slot => (slot.CourtId, (int)(slot.StartsAt - from).TotalHours))
            .ToHashSet();
    }
}
