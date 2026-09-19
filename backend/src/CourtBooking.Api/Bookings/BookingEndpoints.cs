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
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        if (await PublicVenueEndpoints.ApprovedAsync(database, request.VenueId, cancellationToken)
            is not { } venue)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, VenueErrorCodes.NotFound);
        }

        // Hours whose hold is over go back on sale before anything is read or written, because the
        // database's view of them is what decides whether this booking can have them (PRD 9.2).
        await BookedSlots.ReleaseLapsedAsync(database, now, cancellationToken);

        // One hold at a time, so an abandoned pick cannot sit on hours nobody is paying for
        // (PRD S-22).
        if (await BookedSlots.LiveHolds(database, now)
            .AnyAsync(held => held.BookerUserId == bookerId, cancellationToken))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, BookingErrorCodes.AlreadyHolding);
        }

        var priced = await PriceSlotsAsync(database, venue.Id, slots, now, cancellationToken);
        if (priced.Error is { } unavailable)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, unavailable);
        }

        var booking = Booking.Hold(
            venue.Id,
            bookerId,
            await InForcePolicyIdAsync(database, venue.Id, cancellationToken),
            priced.Slots,
            now);
        database.Bookings.Add(booking);

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException failure)
            when (failure.InnerException is PostgresException { SqlState: ExclusionViolation })
        {
            // Someone took the same hour between the grid being read and this write. The whole
            // booking is refused rather than partly made (PRD BR-04).
            return ApiProblem.Of(StatusCodes.Status409Conflict, BookingErrorCodes.SlotJustTaken);
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
