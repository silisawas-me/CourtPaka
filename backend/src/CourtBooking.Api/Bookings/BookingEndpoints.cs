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

    /// <summary>
    /// How much of their own history one answer carries. A booker who has played every week for
    /// years has hundreds of them; what they opened the page for is at the top.
    /// </summary>
    public const int MaxHistory = 200;

    public static void MapBookingEndpoints(this IEndpointRouteBuilder routes)
    {
        var bookings = routes.MapGroup("/bookings").WithTags("Bookings").RequireAuthorization();

        bookings.MapPost("/", CreateAsync);
        bookings.MapGet("/", MineAsync);
        bookings.MapGet("/{bookingId:guid}", GetAsync);
        bookings.MapPost("/{bookingId:guid}/cancel", CancelAsync);
        bookings.MapSlipEndpoints();
    }

    /// <summary>The booker's own booking. Someone else's answers the same as one that is not there.</summary>
    private static async Task<Results<Ok<BookingResponse>, ProblemHttpResult>> GetAsync(
        Guid bookingId,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var bookerId = CallerId.Of(principal);
        var mine = await database.Bookings.AnyAsync(
            booking => booking.Id == bookingId && booking.BookerUserId == bookerId,
            cancellationToken);

        return mine
            ? TypedResults.Ok(await ReadBookingAsync(
                database, bookingId, timeProvider.GetUtcNow(), cancellationToken))
            : ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
    }

    /// <summary>
    /// Everything this booker has taken, split into what is still ahead of them and what is behind
    /// (PRD US-05). One request: a history page that had to ask again for every booking would be
    /// slower than the grid it came from.
    /// </summary>
    private static async Task<Ok<BookingHistoryResponse>> MineAsync(
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var bookerId = CallerId.Of(principal);

        // Newest first and capped: a booker who has played for years should not be handed all of
        // it at once, and what they came to look at is at the top. Paging is its own story.
        var mine = await database.Bookings
            .AsNoTracking()
            .Include(booking => booking.Slots)
            .Include(booking => booking.CancellationPolicy!)
            .ThenInclude(policy => policy.Tiers)
            .Where(booking => booking.BookerUserId == bookerId)
            .OrderByDescending(booking => booking.CreatedAt)
            .Take(MaxHistory)
            .ToListAsync(cancellationToken);

        var venueIds = mine.Select(booking => booking.VenueId).Distinct().ToArray();

        var venueNames = await database.Venues
            .Where(venue => venueIds.Contains(venue.Id))
            .ToDictionaryAsync(venue => venue.Id, venue => venue.Name, cancellationToken);

        var courtNames = await database.Courts
            .Where(court => venueIds.Contains(court.VenueId))
            .ToDictionaryAsync(court => court.Id, court => court.Name, cancellationToken);

        var bookingIds = mine.Select(booking => booking.Id).ToArray();
        var slipTimes = await database.PaymentSlips
            .Where(slip => bookingIds.Contains(slip.BookingId))
            .GroupBy(slip => slip.BookingId)
            .Select(slips => new
            {
                BookingId = slips.Key,
                Latest = slips.Max(slip => slip.UploadedAt),
            })
            .ToDictionaryAsync(row => row.BookingId, row => row.Latest, cancellationToken);

        var upcoming = new List<BookingResponse>();
        var past = new List<BookingResponse>();

        foreach (var booking in mine)
        {
            // A hold whose time is up reads as Expired here as everywhere else (PRD 9.2).
            var status = BookedSlots.HasLapsed(booking, now) ? BookingStatus.Expired : booking.Status;
            var response = ToResponse(
                booking,
                venueNames.GetValueOrDefault(booking.VenueId, string.Empty),
                courtNames,
                now,
                status,
                slipTimes.TryGetValue(booking.Id, out var uploaded) ? uploaded : null);

            (Ahead(status, booking, now) ? upcoming : past).Add(response);
        }

        // Ahead of them: soonest first, because the next one to be played is the one being looked
        // for. Behind them stays newest first, which is the order they happened in, backwards.
        upcoming.Reverse();

        return TypedResults.Ok(new BookingHistoryResponse([.. upcoming], [.. past]));
    }

    /// <summary>
    /// Whether this booking is still ahead of the booker: hours that have not been played, held by
    /// a booking that still holds them. Everything else — cancelled, refused, lapsed, played — is
    /// history, whatever day it falls on (PRD US-05).
    /// </summary>
    private static bool Ahead(BookingStatus status, Booking booking, DateTimeOffset now) =>
        status is BookingStatus.Held or BookingStatus.PendingVerification or BookingStatus.Confirmed
        && booking.Slots.Count > 0
        && booking.Slots.Max(slot => slot.EndsAt) > now;

    /// <summary>
    /// The booker lets the hours go (PRD US-05). What comes back is decided here and not by the
    /// page that asked, so the number shown before the button and the number written after it are
    /// the same one.
    /// </summary>
    private static async Task<Results<Ok<BookingResponse>, ProblemHttpResult>> CancelAsync(
        Guid bookingId,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var bookerId = CallerId.Of(principal);

        var booking = await database.Bookings
            .AsNoTracking()
            .Include(candidate => candidate.Slots)
            .Include(candidate => candidate.CancellationPolicy!)
            .ThenInclude(policy => policy.Tiers)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == bookingId && candidate.BookerUserId == bookerId,
                cancellationToken);

        // Someone else's booking answers the same as one that is not there.
        if (booking is null || booking.Slots.Count == 0)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, BookingErrorCodes.NotFound);
        }

        var status = BookedSlots.HasLapsed(booking, now) ? BookingStatus.Expired : booking.Status;
        var offer = Cancellation.For(
            booking,
            status,
            booking.CancellationPolicy,
            booking.Slots.Min(slot => slot.StartsAt),
            now);

        if (offer.Refused is { } refused)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, refused);
        }

        var payment = Cancellation.PaymentAfter(status, booking.PaymentState);
        var refundDue = Refunds.DueFor(
            BookingStatus.Cancelled, payment, booking.TotalBaht, offer.RefundPercent);

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        // The status was read a moment ago and the venue may have decided in between. The move
        // carries its condition, so a booking that has moved on is left alone (PRD 6.1).
        var moving = database.Bookings.Where(candidate =>
            candidate.Id == bookingId
            && candidate.BookerUserId == bookerId
            && candidate.Status == status);

        // Letting go of a hold is only letting go while it is still a hold (PRD BR-02).
        if (status == BookingStatus.Held)
        {
            moving = moving.Where(candidate => candidate.HoldExpiresAt > now);
        }

        var moved = await moving.ExecuteUpdateAsync(
            set => set
                .SetProperty(candidate => candidate.Status, BookingStatus.Cancelled)
                .SetProperty(candidate => candidate.PaymentState, payment)
                .SetProperty(candidate => candidate.RefundPercent, offer.RefundPercent)
                .SetProperty(candidate => candidate.RefundDueBaht, refundDue),
            cancellationToken);

        if (moved == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status409Conflict, BookingErrorCodes.NotCancellable);
        }

        // The hours go back on sale the moment they are given up (PRD 6.1).
        await database.BookingSlots
            .Where(slot => slot.BookingId == bookingId && slot.IsActive)
            .ExecuteUpdateAsync(
                set => set.SetProperty(slot => slot.IsActive, false),
                cancellationToken);

        database.BookingStatusChanges.Add(BookingTransitions.Record(
            bookingId, status, BookingStatus.Cancelled, bookerId, now));

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // The name is part of the template, not a parameter, so a sink that groups by template
        // counts these apart from every other move (PRD 8).
        AppEvents.For(loggers).LogInformation(
            "booking_cancelled {BookingId} {VenueId} {From} {RefundDueBaht} {AwaitsVenue}",
            bookingId,
            booking.VenueId,
            status,
            refundDue,
            offer.AwaitsVenue);

        return TypedResults.Ok(await ReadBookingAsync(
            database, bookingId, now, cancellationToken));
    }

    /// <summary>
    /// A booking as its booker sees it, read back from the database so it says what was stored
    /// rather than what was asked for.
    /// </summary>
    internal static async Task<BookingResponse> ReadBookingAsync(
        AppDbContext database,
        Guid bookingId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var booking = await database.Bookings
            .AsNoTracking()
            .Include(candidate => candidate.Slots)
            .Include(candidate => candidate.CancellationPolicy!)
            .ThenInclude(policy => policy.Tiers)
            .SingleAsync(candidate => candidate.Id == bookingId, cancellationToken);

        var venueName = await database.Venues
            .Where(venue => venue.Id == booking.VenueId)
            .Select(venue => venue.Name)
            .SingleAsync(cancellationToken);

        var courtNames = await database.Courts
            .Where(court => court.VenueId == booking.VenueId)
            .ToDictionaryAsync(court => court.Id, court => court.Name, cancellationToken);

        var slipUploadedAt = await database.PaymentSlips
            .Where(slip => slip.BookingId == booking.Id)
            .NewestFirst()
            .Select(slip => (DateTimeOffset?)slip.UploadedAt)
            .FirstOrDefaultAsync(cancellationToken);

        // A hold whose time is up reads as Expired whether or not anything has written that yet,
        // the same way every other query treats one (PRD 9.2).
        var status = BookedSlots.HasLapsed(booking, now) ? BookingStatus.Expired : booking.Status;

        return ToResponse(booking, venueName, courtNames, now, status, slipUploadedAt);
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
        Expired(loggers, await BookedSlots.ReleaseOwnLapsedAsync(
            database, bookerId, now, cancellationToken));

        // One hold at a time, so an abandoned pick cannot sit on hours nobody is paying for
        // (PRD S-22).
        if (await BookedSlots.LiveHolds(database, now)
            .AnyAsync(held => held.BookerUserId == bookerId, cancellationToken))
        {
            return Refuse(
                loggers, StatusCodes.Status409Conflict, BookingErrorCodes.AlreadyHolding, bookerId);
        }

        var priced = await PriceSlotsAsync(
            database, venue.Id, slots, now, loggers, cancellationToken);
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
            ToResponse(booking, venue.Name, priced.CourtNames, now));
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
    /// Says that holds ran out. A status change is an event whoever is watching should see, and
    /// this is the one nobody asks for (PRD 8, 6.1).
    /// </summary>
    private static void Expired(ILoggerFactory loggers, IReadOnlyList<Guid> bookingIds)
    {
        var events = AppEvents.For(loggers);
        foreach (var bookingId in bookingIds)
        {
            events.LogInformation("booking_expired {BookingId}", bookingId);
        }
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
        ILoggerFactory loggers,
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
        var lapsed = await BookedSlots.ReleaseLapsedAsync(
            database,
            [.. slots.Select(slot => slot.CourtId).Distinct()],
            PlatformRequirements.BangkokHour(date, 0),
            PlatformRequirements.BangkokHour(date.AddDays(1), 0),
            now,
            cancellationToken);
        Expired(loggers, lapsed);

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
        IReadOnlyDictionary<Guid, string> courtNames,
        DateTimeOffset now,
        BookingStatus? status = null,
        DateTimeOffset? slipUploadedAt = null) =>
        new(
            booking.Id,
            booking.VenueId,
            venueName,
            (status ?? booking.Status).ToString(),
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
                .ToArray(),
            slipUploadedAt,
            booking.PaymentState.ToString(),
            booking.RefundDueBaht,
            // Refunds are made by the venue outside the system and written down against the
            // booking, which is US-18. Nothing has been written down yet, so nothing has been
            // sent back (PRD 6.2, BR-06).
            RefundedBaht: 0m,
            Offer(booking, now, status ?? booking.Status));

    /// <summary>
    /// What letting this booking go would come to, for the page to show before anything is
    /// pressed. A booking with no slots or no policy in hand cannot be offered one, which is a
    /// read that did not ask for them rather than a booking that lacks them.
    /// </summary>
    private static CancellationOfferResponse Offer(
        Booking booking,
        DateTimeOffset now,
        BookingStatus status)
    {
        if (booking.Slots.Count == 0)
        {
            return new CancellationOfferResponse(
                false, BookingErrorCodes.NotCancellable, 0, 0m, false);
        }

        var offer = Cancellation.For(
            booking,
            status,
            booking.CancellationPolicy,
            booking.Slots.Min(slot => slot.StartsAt),
            now);

        return new CancellationOfferResponse(
            offer.Allowed, offer.Refused, offer.RefundPercent, offer.RefundBaht, offer.AwaitsVenue);
    }

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
