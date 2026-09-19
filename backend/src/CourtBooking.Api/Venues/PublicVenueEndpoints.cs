using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
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
            var term = $"%{Like(q.Trim())}%";
            query = query.Where(venue =>
                EF.Functions.ILike(venue.Name, term, LikeEscape)
                || EF.Functions.ILike(venue.District, term, LikeEscape)
                || EF.Functions.ILike(venue.Province, term, LikeEscape));
        }

        var found = await query
            .OrderBy(venue => venue.Province)
            .ThenBy(venue => venue.District)
            .ThenBy(venue => venue.Name)
            .Take(MaxResults)
            .Select(venue => new PublicVenueResponse(
                venue.Id, venue.Name, venue.AddressLine, venue.District, venue.Province))
            .ToArrayAsync(cancellationToken);

        return TypedResults.Ok(found);
    }

    /// <summary>
    /// The court-hours of one day, and the venue they belong to, because the page that draws the
    /// grid wants both and one request is one round trip. This is where the booking flow starts, so
    /// it is also where <c>venue_page_viewed</c> is recorded (PRD 8).
    /// </summary>
    private static async Task<Results<Ok<AvailabilityResponse>, ProblemHttpResult>> AvailabilityAsync(
        Guid venueId,
        DateOnly? date,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = PlatformRequirements.BangkokToday(timeProvider);
        var asked = date ?? today;

        var invalid = Availability.ValidateDate(asked, today);
        if (invalid is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        if (await ApprovedAsync(database, venueId, cancellationToken) is not { } venue)
        {
            // A booker following a shared link to a venue that was suspended or never existed. It
            // is the one 404 a booker meets, so it says which one it is (US-23).
            return ApiProblem.Of(StatusCodes.Status404NotFound, VenueErrorCodes.NotFound);
        }

        var day = await VenueDay.LoadAsync(database, venueId, asked, now, cancellationToken);

        AppEvents.For(loggers).LogInformation("venue_page_viewed {VenueId} {Date}", venueId, asked);

        return TypedResults.Ok(Availability.Draw(Public(venue), day, today));
    }

    /// <summary>
    /// The venue a booker is allowed to see. A pending, rejected or suspended one is not one of
    /// them, however it is asked for.
    /// </summary>
    internal static Task<Venue?> ApprovedAsync(
        AppDbContext database,
        Guid venueId,
        CancellationToken cancellationToken) =>
        database.Venues
            .AsNoTracking()
            .SingleOrDefaultAsync(
                venue => venue.Id == venueId && venue.Status == VenueStatus.Approved, cancellationToken);

    private static PublicVenueResponse Public(Venue venue) => new(
        venue.Id, venue.Name, venue.AddressLine, venue.District, venue.Province);

    /// <summary>
    /// Postgres reads %, _ and \ in a LIKE pattern, so a booker typing one would otherwise match
    /// everything rather than look for the character they typed.
    /// </summary>
    private const string LikeEscape = @"\";

    private static string Like(string term) => term
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);
}
