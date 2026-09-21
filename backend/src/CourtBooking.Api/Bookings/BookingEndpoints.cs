using System.Linq.Expressions;
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
        bookings.MapGet("/{bookingId:guid}/payment", PaymentAsync);
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
    /// Everything this booker has taken, split into what is still ahead of them and what is
    /// behind (PRD US-05). One request: a history page that had to ask again for every booking
    /// would be slower than the grid it came from.
    ///
    /// The two are read separately so the ceiling on one cannot eat the other — a booker with
    /// hundreds behind them still sees everything they are about to play.
    /// </summary>
    private static async Task<Ok<BookingHistoryResponse>> MineAsync(
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var bookerId = CallerId.Of(principal);
        var ahead = Ahead(now);

        var mine = database.Bookings.Where(booking => booking.BookerUserId == bookerId);

        // Soonest first, by when it will be played and not by when it was taken: someone who
        // books Saturday today and tomorrow morning five minutes later is looking for tomorrow.
        var upcoming = await ReadManyAsync(
            database,
            mine.Where(ahead).OrderBy(booking => booking.Slots.Min(slot => slot.StartsAt)),
            now,
            cancellationToken);

        // Behind them, newest first, which is the order they happened in, backwards.
        var past = await ReadManyAsync(
            database,
            mine.Where(Not(ahead)).OrderByDescending(booking => booking.CreatedAt),
            now,
            cancellationToken);

        return TypedResults.Ok(new BookingHistoryResponse(upcoming, past));
    }

    /// <summary>
    /// Whether a booking is still ahead of the booker: hours that have not been played, held by a
    /// booking that still holds them. Everything else — cancelled, refused, lapsed, played — is
    /// behind them, whatever day it falls on (PRD US-05).
    ///
    /// Said in a form the database can read, because which list a booking belongs to has to be
    /// settled before either is cut to length. A hold whose fifteen minutes are up is behind them
    /// whether or not anything has written that down, which is what the clause about
    /// <see cref="Booking.HoldExpiresAt"/> says (PRD 9.2).
    /// </summary>
    private static Expression<Func<Booking, bool>> Ahead(DateTimeOffset now) =>
        booking =>
            booking.Slots.Any()
            && booking.Slots.Max(slot => slot.EndsAt) > now
            && (booking.Status == BookingStatus.PendingVerification
                || booking.Status == BookingStatus.Confirmed
                || (booking.Status == BookingStatus.Held && booking.HoldExpiresAt > now));

    /// <summary>
    /// The other half of a predicate, so that the two lists cannot disagree about which booking
    /// belongs where. Every column involved is non-nullable, so there is no third answer for the
    /// negation to swallow.
    /// </summary>
    private static Expression<Func<T, bool>> Not<T>(Expression<Func<T, bool>> predicate) =>
        Expression.Lambda<Func<T, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    /// <summary>
    /// The booker lets the hours go (PRD US-05). What comes back is decided here and not by the
    /// page that asked, so the number shown before the button and the number written after it are
    /// the same one.
    /// </summary>
    private static async Task<Results<Ok<BookingResponse>, ProblemHttpResult>> CancelAsync(
        Guid bookingId,
        ClaimsPrincipal principal,
        AppDbContext database,
        VenueNotifications notifications,
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

        var status = BookedSlots.StatusAt(booking, now);
        var offer = Cancellation.For(booking, status, now);

        if (offer.Refused is { } refused)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, refused);
        }

        var refundDue = Refunds.DueFor(
            BookingStatus.Cancelled, offer.Payment, booking.TotalBaht, offer.RefundPercent);

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        // The hours go back on sale the moment they are given up (PRD 6.1). Before the booking
        // row, which is the order BookedSlots.ReleaseAsync explains and every writer keeps.
        await BookedSlots.ReleaseAsync(database, bookingId, cancellationToken);

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
                .SetProperty(candidate => candidate.PaymentState, offer.Payment)
                .SetProperty(candidate => candidate.RefundPercent, offer.RefundPercent)
                .SetProperty(candidate => candidate.RefundDueBaht, refundDue),
            cancellationToken);

        if (moved == 0)
        {
            // Somebody else moved it between the read and the write — usually the venue deciding
            // about the slip. What it may do now depends on where it has got to, so the page is
            // told to ask again rather than being given a reason that was true a moment ago.
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, BookingErrorCodes.ChangedMeanwhile);
        }

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

        // Money the venue has to move or answer for is money nobody will see unless the venue is
        // told. Whether there is any is not decided here: the same rule has to answer for the
        // number beside the door, so it lives with it (PRD US-17).
        await notifications.MoneyMayBeWaitingAsync(
            booking.VenueId, bookingId, refundDue, offer.Payment);

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
        CancellationToken cancellationToken) =>
        (await ReadManyAsync(
            database,
            database.Bookings.Where(booking => booking.Id == bookingId),
            now,
            cancellationToken)).Single();

    /// <summary>
    /// The bookings a query selects, each as its booker sees it. Names live on the venue and its
    /// courts rather than on the booking, so they are fetched once for the whole set instead of
    /// once per booking.
    /// </summary>
    private static async Task<BookingResponse[]> ReadManyAsync(
        AppDbContext database,
        IQueryable<Booking> bookings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var found = await bookings
            .AsNoTracking()
            .Include(booking => booking.Slots)
            .Include(booking => booking.CancellationPolicy!)
            .ThenInclude(policy => policy.Tiers)
            .Take(MaxHistory)
            .ToListAsync(cancellationToken);

        if (found.Count == 0)
        {
            return [];
        }

        var venueIds = found.Select(booking => booking.VenueId).Distinct().ToArray();
        var bookingIds = found.Select(booking => booking.Id).ToArray();

        var venueNames = await database.Venues
            .Where(venue => venueIds.Contains(venue.Id))
            .ToDictionaryAsync(venue => venue.Id, venue => venue.Name, cancellationToken);

        var courtNames = await database.Courts
            .Where(court => venueIds.Contains(court.VenueId))
            .ToDictionaryAsync(court => court.Id, court => court.Name, cancellationToken);

        // What the venue has actually sent back, per booking: the sum of the records that still
        // stand (PRD 6.2, US-18).
        var sentBack = await database.RefundRecords
            .StillStanding()
            .Where(record => bookingIds.Contains(record.BookingId))
            .GroupBy(record => record.BookingId)
            .Select(records => new
            {
                BookingId = records.Key,
                Baht = records.Sum(record => record.AmountBaht),
            })
            .ToDictionaryAsync(row => row.BookingId, row => row.Baht, cancellationToken);

        // When the newest slip arrived. The tiebreak that SlipDownload.NewestFirst applies decides
        // which slip is the current one; the latest moment is the same either way, and asking for
        // it per booking rather than per set would be a query each.
        var slipTimes = await database.PaymentSlips
            .Where(slip => bookingIds.Contains(slip.BookingId))
            .GroupBy(slip => slip.BookingId)
            .Select(slips => new
            {
                BookingId = slips.Key,
                Latest = slips.Max(slip => slip.UploadedAt),
            })
            .ToDictionaryAsync(row => row.BookingId, row => row.Latest, cancellationToken);

        return
        [
            .. found.Select(booking => ToResponse(
                booking,
                venueNames[booking.VenueId],
                courtNames,
                now,
                BookedSlots.StatusAt(booking, now),
                slipTimes.TryGetValue(booking.Id, out var uploaded) ? uploaded : null,
                sentBack.GetValueOrDefault(booking.Id))),
        ];
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
        var booker = await database.Users
            .Where(user => user.Id == bookerId)
            .Select(user => new { user.EmailConfirmed, user.SuspendedAt })
            .SingleOrDefaultAsync(cancellationToken);

        // A session opened before a suspension lives until the next revalidation (PRD US-22);
        // the row is being read here anyway, so a suspended account cannot hold courts meanwhile.
        if (booker?.SuspendedAt is not null)
        {
            return Refuse(
                loggers, StatusCodes.Status403Forbidden, AuthErrorCodes.AccountSuspended, bookerId);
        }

        if (booker is not { EmailConfirmed: true })
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

        if (await WriteNewAsync(database, booking, timeProvider, cancellationToken)
            is { } refused)
        {
            return Refuse(loggers, StatusCodes.Status409Conflict, refused, bookerId);
        }

        AppEvents.For(loggers).LogInformation(
            "booking_held {BookingId} {VenueId} {Slots} {TotalBaht}",
            booking.Id,
            venue.Id,
            booking.Slots.Count,
            booking.TotalBaht);

        return TypedResults.Created(
            $"/api/bookings/{booking.Id}",
            ToResponse(booking, venue.Name, priced.CourtNames, now, booking.Status));
    }

    /// <summary>
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
    internal static async Task<PricedSlots> PriceSlotsAsync(
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
    internal static async Task<Guid> InForcePolicyIdAsync(
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

    /// <summary>
    /// How to pay for this booking (PRD US-04): the venue's account, and a QR carrying the exact
    /// amount. Its own endpoint rather than a field on every booking — a QR is only of use while
    /// one booking is waiting to be paid for, and a history list has no business carrying the
    /// venue's bank details on every row.
    /// </summary>
    private static async Task<Results<Ok<PaymentResponse>, NotFound>> PaymentAsync(
        Guid bookingId,
        ClaimsPrincipal principal,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var bookerId = CallerId.Of(principal);

        var paying = await database.Bookings
            .AsNoTracking()
            .Where(booking => booking.Id == bookingId && booking.BookerUserId == bookerId)
            .Select(booking => new
            {
                booking.TotalBaht,
                booking.HoldExpiresAt,
                Account = booking.Venue!.Business.PromptPayId,
                AccountName = booking.Venue!.Business.PromptPayAccountName,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (paying is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new PaymentResponse(
            paying.TotalBaht,
            paying.HoldExpiresAt,
            paying.AccountName,
            // Null where the venue's account is not something a bank app would take. The page
            // says so rather than drawing a code that is refused at the counter (PRD US-10).
            PromptPay.For(paying.Account, paying.TotalBaht)));
    }

    /// <summary>
    /// Writes a new booking and its hours, or says why it could not. Both doors that take hours —
    /// the booker's and the counter's — come through here, so they queue for the same hours the
    /// same way and are refused by the same constraint in the same words (PRD BR-04, US-13).
    /// </summary>
    /// <returns>Null once it is written; otherwise the code it was refused with.</returns>
    internal static async Task<string?> WriteNewAsync(
        AppDbContext database,
        Booking booking,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            database.ChangeTracker.Clear();

            await using var transaction =
                await database.Database.BeginTransactionAsync(cancellationToken);

            // A booker who was suspended or asked to be forgotten a moment ago may still hold a
            // session; the row is what says, and share-locking it queues this behind a deletion
            // in progress (PRD US-22, S-15). Counter bookings have no booker to ask about.
            if (booking.BookerUserId is { } bookerUserId
                && await AccountGate.RefusalAsync(database, bookerUserId, cancellationToken) is { } closed)
            {
                return closed;
            }

            // Queue for the hours being taken, in one fixed order, before touching the index that
            // guards them. Without this, people reaching for the same hour collide inside the
            // exclusion constraint's index and Postgres breaks the standoff by killing one of them
            // with a deadlock — a 500, where waiting a moment gives a real answer. The constraint
            // is still what guarantees the rule; this only decides who asks it first.
            await BookedSlots.LockAsync(database, booking.Slots, cancellationToken);

            database.Bookings.Add(booking);

            try
            {
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            catch (DbUpdateException failure)
                when (failure.InnerException is PostgresException { SqlState: ExclusionViolation })
            {
                // Someone took the same hour between the grid being read and this write. The whole
                // booking is refused rather than partly made (PRD BR-04).
                return BookingErrorCodes.SlotJustTaken;
            }
            catch (DbUpdateException failure)
                when (failure.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                // The booker's other request won the race to hold something (PRD S-22). The read
                // before this answers it for one request at a time; the index answers it for two.
                return BookingErrorCodes.AlreadyHolding;
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
    }

    private static BookingResponse ToResponse(
        Booking booking,
        string venueName,
        IReadOnlyDictionary<Guid, string> courtNames,
        DateTimeOffset now,
        BookingStatus status,
        DateTimeOffset? slipUploadedAt = null,
        decimal sentBackBaht = 0m) =>
        new(
            booking.Id,
            booking.VenueId,
            venueName,
            status.ToString(),
            booking.CreatedAt,
            booking.HoldExpiresAt,
            booking.TotalBaht,
            BookingSlotResponse.Of(booking, courtNames),
            slipUploadedAt,
            booking.PaymentState.ToString(),
            booking.RefundDueBaht,
            // Made outside this system and written down after the fact, so this is the sum of
            // what the venue says it sent (PRD 6.2, BR-06, US-18).
            sentBackBaht,
            Offer(booking, now, status));

    /// <summary>What letting this booking go would come to, as the page reads it.</summary>
    private static CancellationOfferResponse Offer(
        Booking booking,
        DateTimeOffset now,
        BookingStatus status)
    {
        var offer = Cancellation.For(booking, status, now);

        return new CancellationOfferResponse(
            offer.Allowed, offer.RefundPercent, offer.RefundBaht, offer.AwaitsVenue);
    }

    /// <summary>The hours as priced, or the reason none of them can be had.</summary>
    internal readonly record struct PricedSlots(
        SlotPrice[] Slots,
        Dictionary<Guid, string> CourtNames,
        string? Error)
    {
        public static PricedSlots Refused(Dictionary<Guid, string> courtNames, string error) =>
            new([], courtNames, error);
    }
}
