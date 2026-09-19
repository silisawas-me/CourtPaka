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

        var venue = await database.Venues
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == request.VenueId
                    && candidate.Status == VenueStatus.Approved,
                cancellationToken);

        if (venue is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, VenueErrorCodes.NotFound);
        }

        // One hold at a time, so an abandoned pick cannot sit on hours nobody is paying for
        // (PRD S-22). An expired hold is not a hold, whether or not the job has caught it.
        if (await database.Bookings.AnyAsync(
                held => held.BookerUserId == bookerId
                    && held.Status == BookingStatus.Held
                    && held.HoldExpiresAt > now,
                cancellationToken))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, BookingErrorCodes.AlreadyHolding);
        }

        var priced = await PriceSlotsAsync(database, venue.Id, slots, now, today, cancellationToken);
        if (priced.Error is { } unavailable)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, unavailable);
        }

        var policy = await PricingEndpoints.InForcePolicyAsync(database, venue.Id, cancellationToken);
        if (policy is null)
        {
            // Every venue is created with the default policy, and policies are never deleted.
            return ApiProblem.Of(StatusCodes.Status409Conflict, BookingErrorCodes.HourNotAvailable);
        }

        var booking = Booking.Hold(venue.Id, bookerId, policy.Id, priced.Slots, now);
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
    /// What each picked hour costs, read from the same settings the grid was drawn from. An hour
    /// the grid would not have offered is refused here too, because the grid may be minutes old.
    /// </summary>
    private static async Task<(SlotPrice[] Slots, Dictionary<Guid, string> CourtNames, string? Error)>
        PriceSlotsAsync(
            AppDbContext database,
            Guid venueId,
            IReadOnlyCollection<BookingSlotRequest> slots,
            DateTimeOffset now,
            DateOnly today,
            CancellationToken cancellationToken)
    {
        var courts = await database.Courts
            .AsNoTracking()
            .Where(court => court.VenueId == venueId)
            .ToListAsync(cancellationToken);

        var names = courts.ToDictionary(court => court.Id, court => court.Name);
        var priced = new List<SlotPrice>(slots.Count);

        foreach (var day in slots.GroupBy(slot => slot.Date))
        {
            var grid = await DayAsync(database, venueId, courts, day.Key, now, today, cancellationToken);

            foreach (var slot in day)
            {
                var court = grid.Courts.SingleOrDefault(row => row.CourtId == slot.CourtId);
                var hour = court?.Hours.SingleOrDefault(cell => cell.Hour == slot.Hour);

                if (hour is null || hour.Status != nameof(HourStatus.Free) || hour.BahtPerHour is null)
                {
                    var taken = hour?.Status == nameof(HourStatus.Booked);
                    return (
                        [],
                        names,
                        taken ? BookingErrorCodes.SlotJustTaken : BookingErrorCodes.HourNotAvailable);
                }

                priced.Add(new SlotPrice(
                    slot.CourtId,
                    PlatformRequirements.BangkokHour(slot.Date, slot.Hour),
                    hour.BahtPerHour.Value));
            }
        }

        return (priced.ToArray(), names, null);
    }

    /// <summary>The grid for one day, built the same way the public endpoint builds it.</summary>
    private static async Task<AvailabilityResponse> DayAsync(
        AppDbContext database,
        Guid venueId,
        IReadOnlyCollection<Court> courts,
        DateOnly date,
        DateTimeOffset now,
        DateOnly today,
        CancellationToken cancellationToken) =>
        Availability.Build(
            new PublicVenueResponse(venueId, string.Empty, string.Empty, string.Empty, string.Empty),
            date,
            today,
            courts,
            await CourtEndpoints.StatusChangesAsync(database, venueId, date, cancellationToken),
            await CourtEndpoints.ScheduleOnAsync(database, venueId, date, cancellationToken),
            (await PricingEndpoints.InForcePricesAsync(database, venueId, cancellationToken))?.Bands ?? [],
            await BookedSlots.OnAsync(database, venueId, date, now, cancellationToken));

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
}
