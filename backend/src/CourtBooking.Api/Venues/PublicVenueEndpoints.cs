using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Localization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>
/// What someone who has not signed in can see: the venues on the platform and when their courts
/// are free. Signing in is for booking, not for looking (PRD US-02).
///
/// Only approved venues appear here. A pending, rejected or suspended venue is invisible to a
/// booker however it is asked for (PRD US-10, US-20).
/// </summary>
public static class PublicVenueEndpoints
{
    /// <summary>A page of results is enough to choose from without being a scrape.</summary>
    public const int MaxResults = 50;

    public static void MapPublicVenueEndpoints(this IEndpointRouteBuilder routes)
    {
        var venues = routes.MapGroup("/venues").WithTags("Venues").AllowAnonymous();

        venues.MapGet("/search", SearchAsync);
        venues.MapGet("/{venueId:guid}/public", GetAsync);
        venues.MapGet("/{venueId:guid}/availability", AvailabilityAsync);
    }

    /// <summary>
    /// Venues whose name, district or province contains what was typed. An empty query lists them
    /// all, which is what the page shows before anyone types.
    /// </summary>
    private static async Task<Ok<PublicVenueResponse[]>> SearchAsync(
        string? q,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var query = database.Venues.AsNoTracking().Where(venue => venue.Status == VenueStatus.Approved);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(venue =>
                EF.Functions.ILike(venue.Name, $"%{term}%")
                || EF.Functions.ILike(venue.District, $"%{term}%")
                || EF.Functions.ILike(venue.Province, $"%{term}%"));
        }

        var found = await query
            .OrderBy(venue => venue.Province)
            .ThenBy(venue => venue.District)
            .ThenBy(venue => venue.Name)
            .Take(MaxResults)
            .Select(venue => new PublicVenueResponse(
                venue.Id, venue.Code, venue.Name, venue.AddressLine, venue.District, venue.Province))
            .ToArrayAsync(cancellationToken);

        return TypedResults.Ok(found);
    }

    private static async Task<Results<Ok<PublicVenueResponse>, NotFound>> GetAsync(
        Guid venueId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var venue = await ApprovedAsync(database, venueId, cancellationToken);

        return venue is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new PublicVenueResponse(
                venue.Id, venue.Code, venue.Name, venue.AddressLine, venue.District, venue.Province));
    }

    /// <summary>
    /// The court-hours of one day. This is the page the booking flow starts from, so it is also
    /// where <c>venue_page_viewed</c> is recorded (PRD 8).
    /// </summary>
    private static async Task<Results<Ok<AvailabilityResponse>, NotFound, ProblemHttpResult>> AvailabilityAsync(
        Guid venueId,
        DateOnly? date,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);
        var asked = date ?? today;

        var invalid = Availability.ValidateDate(asked, today);
        if (invalid is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        if (await ApprovedAsync(database, venueId, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var courts = await database.Courts
            .AsNoTracking()
            .Where(court => court.VenueId == venueId)
            .ToListAsync(cancellationToken);

        var statusChanges = await database.CourtStatusChanges
            .AsNoTracking()
            .Where(change => change.Court!.VenueId == venueId && change.EffectiveFrom <= asked)
            .ToListAsync(cancellationToken);

        var schedules = await CourtEndpoints.SchedulesAsync(database, venueId, cancellationToken);
        var prices = await PricingEndpoints.InForcePricesAsync(database, venueId, cancellationToken);

        loggers.CreateLogger("CourtBooking.Events").LogInformation(
            "venue_page_viewed {VenueId} {Date}", venueId, asked);

        return TypedResults.Ok(Availability.Build(
            asked,
            courts,
            statusChanges,
            VenueTimeline.OpeningHoursOn(schedules, asked),
            prices?.Bands ?? []));
    }

    private static Task<Venue?> ApprovedAsync(
        AppDbContext database,
        Guid venueId,
        CancellationToken cancellationToken) =>
        database.Venues
            .AsNoTracking()
            .SingleOrDefaultAsync(
                venue => venue.Id == venueId && venue.Status == VenueStatus.Approved, cancellationToken);
}
