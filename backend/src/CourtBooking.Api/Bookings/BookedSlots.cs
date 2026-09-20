using CourtBooking.Api.Data;
using CourtBooking.Api.Localization;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    /// <summary>
    /// What a booking reads as right now. PRD 9.2 names two readings that no job has to have run
    /// for: a hold whose fifteen minutes are up is Expired, and a confirmed booking whose hours
    /// have been played is Completed. Every question asked of a booking is asked of this.
    ///
    /// It needs the booking's slots in hand; a read that did not ask for them gets the stored
    /// status, which is what it asked for.
    /// </summary>
    public static BookingStatus StatusAt(Booking booking, DateTimeOffset now) =>
        HasLapsed(booking, now) ? BookingStatus.Expired
        : HasBeenPlayed(booking, now) ? BookingStatus.Completed
        : booking.Status;

    /// <summary>Whether a confirmed booking's hours are behind it (PRD 6.1, 9.2).</summary>
    public static bool HasBeenPlayed(Booking booking, DateTimeOffset now) =>
        booking.Status == BookingStatus.Confirmed
        && booking.Slots.Count > 0
        && booking.Slots.Max(slot => slot.EndsAt) <= now;

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
    /// Lets go of the hours one booking claims, so they are on sale again the moment it ends
    /// (PRD 6.1). The exclusion constraint reads only <see cref="BookingSlot.IsActive"/>, so a
    /// booking that has ended still holds its court until this has run.
    ///
    /// **Call this before writing the booking row, never after.** Releasing lapsed holds has to
    /// take the slots first — it finds out from them which holds lost one — and two writers that
    /// take the same two tables in opposite orders are a standoff that Postgres settles by
    /// killing one of them. Every write that touches both follows that order.
    /// </summary>
    public static Task<int> ReleaseAsync(
        AppDbContext database,
        Guid bookingId,
        CancellationToken cancellationToken) =>
        ReleaseWhereAsync(database, bookingId, _ => true, cancellationToken);

    /// <summary>
    /// Lets go of the hours a booking has not finished, keeping the ones it has. What a no-show
    /// did not turn up for goes back on sale (PRD BR-04) — including the hour already running,
    /// because a walk-in standing at the counter may have the rest of it (PRD US-13). Only the
    /// hours entirely behind them stay theirs.
    /// </summary>
    public static Task<int> ReleaseRemainingAsync(
        AppDbContext database,
        Guid bookingId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ReleaseWhereAsync(database, bookingId, slot => slot.EndsAt > now, cancellationToken);

    /// <summary>
    /// Lets go of the hours of one booking that a rule picks out, behind the same locks everyone
    /// reaching for those court-hours takes. Letting go touches the same index that claiming
    /// does, so a writer that skipped the queue would meet a claimer inside it (PRD BR-04).
    /// </summary>
    private static async Task<int> ReleaseWhereAsync(
        AppDbContext database,
        Guid bookingId,
        Func<BookingSlot, bool> chosen,
        CancellationToken cancellationToken)
    {
        var holding = await database.BookingSlots
            .AsNoTracking()
            .Where(slot => slot.BookingId == bookingId && slot.IsActive)
            .ToListAsync(cancellationToken);

        var letting = holding.Where(chosen).ToList();
        if (letting.Count == 0)
        {
            return 0;
        }

        await LockAsync(database, letting, cancellationToken);

        var ids = letting.Select(slot => slot.Id).ToArray();
        return await database.BookingSlots
            .Where(slot => ids.Contains(slot.Id) && slot.IsActive)
            .ExecuteUpdateAsync(
                set => set.SetProperty(slot => slot.IsActive, false),
                cancellationToken);
    }

    /// <summary>
    /// Claims a booking's hours again, for a venue taking back a no-show it recorded by mistake
    /// (PRD 6.1). Answers whether it could: somebody else may have taken them in the meantime,
    /// and the exclusion constraint is what says so rather than a query that could be stale.
    /// </summary>
    public static async Task<bool> TakeBackAsync(
        AppDbContext database,
        Guid bookingId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var hours = await database.BookingSlots
            .AsNoTracking()
            .Where(slot => slot.BookingId == bookingId)
            .ToListAsync(cancellationToken);

        if (hours.Count == 0)
        {
            return false;
        }

        // Claiming hours is writing into the same index everyone else books through, so it takes
        // the same locks in the same order — without them two writers meet inside the index and
        // Postgres answers with a deadlock, which is a 500 where this has a real answer.
        await LockAsync(database, hours, cancellationToken);

        // A hold whose fifteen minutes are up holds nothing (PRD 9.2), but the constraint reads
        // only IsActive, so one left sitting there would refuse a correction the rules allow.
        await ReleaseLapsedAsync(
            database,
            [.. hours.Select(slot => slot.CourtId).Distinct()],
            hours.Min(slot => slot.StartsAt),
            hours.Max(slot => slot.EndsAt),
            now,
            cancellationToken);

        try
        {
            await database.BookingSlots
                .Where(slot => slot.BookingId == bookingId && !slot.IsActive)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(slot => slot.IsActive, true),
                    cancellationToken);
            return true;
        }
        // ExecuteUpdate runs its own statement, so the constraint arrives bare rather than
        // wrapped the way SaveChanges wraps one.
        catch (PostgresException failure) when (failure.SqlState == ExclusionViolation)
        {
            return false;
        }
        catch (DbUpdateException failure) when (failure.InnerException is PostgresException
        {
            SqlState: ExclusionViolation,
        })
        {
            return false;
        }
    }

    /// <summary>Postgres raises this when the no-overlap constraint refuses a row (PRD BR-04).</summary>
    private const string ExclusionViolation = "23P01";

    /// <summary>
    /// Queues behind anyone else reaching for these court-hours, in an order everyone agrees on,
    /// so that two writers meet here rather than inside the exclusion constraint's index — where
    /// Postgres breaks the tie by killing one of them (PRD BR-04).
    /// </summary>
    public static async Task LockAsync(
        AppDbContext database,
        IEnumerable<BookingSlot> slots,
        CancellationToken cancellationToken)
    {
        foreach (var key in slots.Select(LockKey).Order())
        {
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({key})", cancellationToken);
        }
    }

    /// <summary>
    /// One number per court-hour, the same for everyone asking for it. Two different hours sharing
    /// a number only means they queue behind each other, which costs a moment and nothing else.
    /// </summary>
    private static long LockKey(BookingSlot slot)
    {
        Span<byte> id = stackalloc byte[16];
        slot.CourtId.TryWriteBytes(id);
        return BitConverter.ToInt64(id[..8]) ^ BitConverter.ToInt64(id[8..]) ^ slot.StartsAt.UtcTicks;
    }

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
    public static async Task<IReadOnlyList<Guid>> ReleaseLapsedAsync(
        AppDbContext database,
        IReadOnlyCollection<Guid> courtIds,
        DateTimeOffset from,
        DateTimeOffset until,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (courtIds.Count == 0)
        {
            return [];
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
            return [];
        }

        // Only the holds that just lost a slot, and only once there is something to say about them.
        return await ExpireAsync(
            database,
            lapsed.Where(booking => !booking.Slots.Any(slot => slot.IsActive)),
            now,
            cancellationToken);
    }

    /// <summary>
    /// Every hold whose time is up, wherever it is. The writes release what is in their own way
    /// because they must, and that is enough to keep the grid honest for hours somebody asks
    /// about; this is for the hours nobody asks about (PRD 9.2, BR-02).
    ///
    /// No advisory locks and no court list: releasing a slot only ever clears <c>IsActive</c>,
    /// which the exclusion constraint reads as the row going away, so it cannot collide with
    /// somebody taking the hour — it can only get out of their way sooner.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> ReleaseAllLapsedAsync(
        AppDbContext database,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var lapsed = Lapsed(database, now);

        var released = await database.BookingSlots
            .Where(slot => slot.IsActive && lapsed.Any(over => over.Id == slot.BookingId))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(slot => slot.IsActive, false), cancellationToken);

        if (released == 0)
        {
            return [];
        }

        return await ExpireAsync(
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
    public static async Task<IReadOnlyList<Guid>> ReleaseOwnLapsedAsync(
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

        return await ExpireAsync(database, mine, now, cancellationToken);
    }

    /// <summary>
    /// Held → Expired for a set of bookings, moved and recorded together (PRD 6.1). Answers which
    /// ones lapsed, so the caller can say so.
    /// </summary>
    private static Task<IReadOnlyList<Guid>> ExpireAsync(
        AppDbContext database,
        IQueryable<Booking> lapsed,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        BookingTransitions.MoveAllAsync(
            database,
            lapsed,
            BookingStatus.Held,
            BookingStatus.Expired,
            // Nobody let it lapse; the clock did.
            byUserId: null,
            now,
            cancellationToken);

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
