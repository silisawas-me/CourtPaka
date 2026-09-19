using System.Security.Claims;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// Taking court-hours (PRD US-03). A booking belongs to the person who made it rather than to the
/// venue, so these hang off /bookings and take a signed-in booker, not venue membership.
/// </summary>
public static class BookingEndpoints
{
    /// <summary>Postgres raises this when an exclusion constraint refuses a row.</summary>
    private const string ExclusionViolation = "23P01";

    /// <summary>And this when a unique index does.</summary>
    private const string UniqueViolation = "23505";

    /// <summary>
    /// And this when it breaks a standoff by killing one of the transactions involved. Two bookers
    /// reaching for the same hour meet at the exclusion constraint, and Postgres resolves that as a
    /// deadlock as readily as a constraint violation. The victim rolled back whole, so its write
    /// can simply be made again — and the second attempt is refused with a reason (PRD BR-04).
    /// </summary>
    private const string Deadlock = "40P01";

    /// <summary>
    /// How many times a deadlocked write is made again. Postgres takes a second to notice a
    /// standoff, so each retry costs the loser about that; the winner pays nothing. Several bookers
    /// on one hour can knock each other over more than once, and being refused with a reason is
    /// worth a few seconds where failing is not.
    /// </summary>
    private const int DeadlockRetries = 4;

    /// <summary>
    /// How long a deadlocked write waits before trying again. Victims all wake at the same instant,
    /// so retrying immediately puts them straight back into each other; a short random pause is
    /// what spreads them out.
    /// </summary>
    private static readonly TimeSpan RetryJitter = TimeSpan.FromMilliseconds(120);

    public static void MapBookingEndpoints(this IEndpointRouteBuilder routes)
    {
        var bookings = routes.MapGroup("/bookings").WithTags("Bookings").RequireAuthorization();

        bookings.MapPost("/", CreateAsync);
    }

    /// <summary>
    /// Holds the hours picked, at the prices and under the cancellation terms in force right now
    /// (PRD BR-05). Paying for the hold is US-04; until then it lapses on its own (PRD BR-02).
    /// </summary>
    private static async Task<Results<Created<BookingResponse>, ProblemHttpResult>> CreateAsync(
        CreateBookingRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = PlatformRequirements.BangkokToday(timeProvider);
        var bookerId = CallerId.Of(principal);
        var slots = request.Slots ?? [];

        if (BookingValidation.Validate(slots, now, today) is { } invalid)
        {
            return Refuse(loggers, StatusCodes.Status400BadRequest, invalid, bookerId);
        }

        // An address nobody has proved they can read is not enough to hold a court (PRD US-01).
        // Signing in unverified is allowed on purpose; this is the gate that is not.
        if (!await database.Users
            .Where(user => user.Id == bookerId)
            .Select(user => user.EmailConfirmed)
            .SingleOrDefaultAsync(cancellationToken))
        {
            return Refuse(
                loggers, StatusCodes.Status403Forbidden, AuthErrorCodes.EmailNotVerified, bookerId);
        }

        if (await PublicVenueEndpoints.ApprovedAsync(database, request.VenueId, cancellationToken)
            is not { } venue)
        {
            return Refuse(
                loggers, StatusCodes.Status404NotFound, VenueErrorCodes.NotFound, bookerId);
        }

        // This booker's own hold, if they left one to lapse. It has to end before the check below
        // and before the index behind it, neither of which can tell the time (PRD S-22).
        await BookedSlots.ReleaseOwnLapsedAsync(database, bookerId, now, cancellationToken);

        // One hold at a time, so an abandoned pick cannot sit on hours nobody is paying for
        // (PRD S-22).
        if (await BookedSlots.LiveHolds(database, now)
            .AnyAsync(held => held.BookerUserId == bookerId, cancellationToken))
        {
            return Refuse(
                loggers, StatusCodes.Status409Conflict, BookingErrorCodes.AlreadyHolding, bookerId);
        }

        var priced = await PriceSlotsAsync(database, venue.Id, slots, now, cancellationToken);
        if (priced.Error is { } unavailable)
        {
            return Refuse(loggers, StatusCodes.Status409Conflict, unavailable, bookerId);
        }

        var policyId = await InForcePolicyIdAsync(database, venue.Id, cancellationToken);
        var booking = Booking.Hold(venue.Id, bookerId, policyId, priced.Slots, now);

        for (var attempt = 0; ; attempt++)
        {
            database.ChangeTracker.Clear();

            await using var transaction =
                await database.Database.BeginTransactionAsync(cancellationToken);

            // Queue for the hours being taken, in one fixed order, before touching the index that
            // guards them. Without this, bookers reaching for the same hour collide inside the
            // exclusion constraint's index and Postgres breaks the standoff by killing one of them
            // with a deadlock — a 500, where waiting a moment gives a real answer. The constraint
            // is still what guarantees the rule; this only decides who asks it first.
            foreach (var key in booking.Slots.Select(LockKey).Order())
            {
                await database.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock({key})", cancellationToken);
            }

            database.Bookings.Add(booking);

            try
            {
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException failure)
                when (failure.InnerException is PostgresException { SqlState: ExclusionViolation })
            {
                // Someone took the same hour between the grid being read and this write. The whole
                // booking is refused rather than partly made (PRD BR-04).
                return Refuse(
                    loggers,
                    StatusCodes.Status409Conflict,
                    BookingErrorCodes.SlotJustTaken,
                    bookerId);
            }
            catch (DbUpdateException failure)
                when (failure.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                // The booker's other request won the race to hold something (PRD S-22). The read
                // above answers this for one request at a time; the index answers it for two.
                return Refuse(
                    loggers,
                    StatusCodes.Status409Conflict,
                    BookingErrorCodes.AlreadyHolding,
                    bookerId);
            }
            catch (DbUpdateException failure)
                when (failure.InnerException is PostgresException { SqlState: Deadlock }
                    && attempt < DeadlockRetries)
            {
                // The queue above should prevent this; nothing was written, so it is safe to ask
                // again after a moment rather than fail a booking on a transient standoff.
                await Task.Delay(
                    TimeSpan.FromMilliseconds(
                        Random.Shared.Next((int)RetryJitter.TotalMilliseconds) * (attempt + 1)),
                    timeProvider,
                    cancellationToken);
            }
        }

        AppEvents.For(loggers).LogInformation(
            "booking_held {BookingId} {VenueId} {Slots} {TotalBaht}",
            booking.Id,
            venue.Id,
            booking.Slots.Count,
            booking.TotalBaht);

        return TypedResults.Created(
            $"/api/bookings/{booking.Id}",
            ToResponse(booking, venue.Name, priced.CourtNames));
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
    /// A booking that did not happen, and why. How often bookers are turned away, and for which
    /// reason, is the number worth watching once this is in front of people (PRD 8).
    /// </summary>
    private static ProblemHttpResult Refuse(
        ILoggerFactory loggers,
        int status,
        string code,
        Guid bookerId)
    {
        AppEvents.For(loggers).LogInformation(
            "booking_refused {Code} {Status} {BookerUserId}", code, status, bookerId);
        return ApiProblem.Of(status, code);
    }

    /// <summary>
    /// What each picked hour costs, read from the same day the grid was drawn from. An hour the
    /// grid would not have offered is refused here too, because the grid may be minutes old.
    /// </summary>
    private static async Task<PricedSlots> PriceSlotsAsync(
        AppDbContext database,
        Guid venueId,
        IReadOnlyCollection<BookingSlotRequest> slots,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var courts = await database.Courts
            .AsNoTracking()
            .Where(court => court.VenueId == venueId)
            .ToListAsync(cancellationToken);

        var names = courts.ToDictionary(court => court.Id, court => court.Name);
        var priced = new List<SlotPrice>(slots.Count);

        // Hours whose hold is over go back on sale before the day is read, because the database's
        // view of them is what decides whether this booking can have them (PRD 9.2).
        var date = slots.Select(slot => slot.Date).Distinct().Single();
        await BookedSlots.ReleaseLapsedAsync(
            database,
            [.. slots.Select(slot => slot.CourtId).Distinct()],
            PlatformRequirements.BangkokHour(date, 0),
            PlatformRequirements.BangkokHour(date.AddDays(1), 0),
            now,
            cancellationToken);

        foreach (var picked in slots.GroupBy(slot => slot.Date))
        {
            var day = await VenueDay.LoadAsync(
                database, venueId, courts, picked.Key, now, cancellationToken);

            foreach (var slot in picked)
            {
                var status = day.Status(slot.CourtId, slot.Hour);
                if (status != HourStatus.Free || day.Price(slot.CourtId, slot.Hour) is not { } baht)
                {
                    return PricedSlots.Refused(
                        names,
                        status == HourStatus.Booked
                            ? BookingErrorCodes.SlotJustTaken
                            : BookingErrorCodes.HourNotAvailable);
                }

                priced.Add(new SlotPrice(
                    slot.CourtId,
                    PlatformRequirements.BangkokHour(slot.Date, slot.Hour),
                    baht));
            }
        }

        return new PricedSlots([.. priced], names, null);
    }

    /// <summary>
    /// The terms this booking is refunded under (PRD BR-05). Every venue is created with the
    /// default policy and policies are only ever added, so one missing is a broken invariant rather
    /// than something to explain to a booker.
    /// </summary>
    private static async Task<Guid> InForcePolicyIdAsync(
        AppDbContext database,
        Guid venueId,
        CancellationToken cancellationToken) =>
        await database.CancellationPolicies
            .Where(policy => policy.VenueId == venueId)
            .OrderByDescending(policy => policy.CreatedAt)
            .ThenByDescending(policy => policy.Id)
            .Select(policy => (Guid?)policy.Id)
            .FirstOrDefaultAsync(cancellationToken)
        ?? throw new InvalidOperationException(
            $"Venue {venueId} has no cancellation policy; every venue is created with one.");

    private static BookingResponse ToResponse(
        Booking booking,
        string venueName,
        IReadOnlyDictionary<Guid, string> courtNames) =>
        new(
            booking.Id,
            booking.VenueId,
            venueName,
            booking.Status.ToString(),
            booking.CreatedAt,
            booking.HoldExpiresAt,
            booking.TotalBaht,
            booking.Slots
                .OrderBy(slot => slot.StartsAt)
                .ThenBy(slot => courtNames.GetValueOrDefault(slot.CourtId))
                .Select(slot =>
                {
                    var (date, hour) = PlatformRequirements.BangkokDateAndHour(slot.StartsAt);
                    return new BookingSlotResponse(
                        slot.CourtId,
                        courtNames.GetValueOrDefault(slot.CourtId, string.Empty),
                        date,
                        hour,
                        slot.BahtPerHour);
                })
                .ToArray());

    /// <summary>The hours as priced, or the reason none of them can be had.</summary>
    private readonly record struct PricedSlots(
        SlotPrice[] Slots,
        Dictionary<Guid, string> CourtNames,
        string? Error)
    {
        public static PricedSlots Refused(Dictionary<Guid, string> courtNames, string error) =>
            new([], courtNames, error);
    }
}
