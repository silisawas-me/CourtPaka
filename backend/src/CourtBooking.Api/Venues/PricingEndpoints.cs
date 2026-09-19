using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Localization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>
/// What a venue charges and what it gives back on a cancellation (PRD US-11). Both are published as
/// versions: a booking snapshots the pair it was made under (BR-05), so changing them only ever
/// affects bookings made afterwards, and the newest version is simply the one in force.
/// </summary>
public static class PricingEndpoints
{
    public static void MapPricingEndpoints(this RouteGroupBuilder venue)
    {
        venue.MapGet("/prices", GetPricesAsync).RequireAuthorization(VenuePolicies.Member);
        venue.MapPut("/prices", SetPricesAsync).RequireAuthorization(VenuePolicies.Settings);

        venue.MapGet("/cancellation-policy", GetPolicyAsync).RequireAuthorization(VenuePolicies.Member);
        venue.MapPut("/cancellation-policy", SetPolicyAsync).RequireAuthorization(VenuePolicies.Settings);
    }

    private static async Task<Results<Ok<PriceListResponse>, NoContent>> GetPricesAsync(
        Guid venueId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var prices = await InForcePricesAsync(database, venueId, cancellationToken);
        return prices is null ? TypedResults.NoContent() : TypedResults.Ok(ToResponse(prices));
    }

    private static async Task<Results<Ok<PriceListResponse>, ProblemHttpResult>> SetPricesAsync(
        Guid venueId,
        SetPricesRequest request,
        AppDbContext database,
        CurrentVenue currentVenue,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var invalid = PricingValidation.TryReadBands(request.Bands, out var bands);
        if (invalid is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        // Prices are checked against the week the venue is open for today: an hour that is open and
        // has no price would show in the availability grid with nothing to charge (PRD US-11).
        var schedules = await database.OpeningHoursSchedules
            .AsNoTracking()
            .Where(schedule => schedule.VenueId == venueId)
            .Include(schedule => schedule.Days)
            .ToListAsync(cancellationToken);

        var uncovered = PricingValidation.CoversOpeningHours(
            bands, VenueTimeline.OpeningHoursOn(schedules, PlatformRequirements.BangkokToday(timeProvider)));
        if (uncovered is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, uncovered);
        }

        var prices = PriceList.Create(venueId, bands, currentVenue.Require().UserId, timeProvider.GetUtcNow());
        database.PriceLists.Add(prices);
        await database.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(ToResponse(prices));
    }

    private static async Task<Ok<CancellationPolicyResponse>> GetPolicyAsync(
        Guid venueId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var policy = await InForcePolicyAsync(database, venueId, cancellationToken);

        // A venue that has never set one still has terms: the default it started with (PRD S-11).
        return TypedResults.Ok(policy is null
            ? new CancellationPolicyResponse(
                Guid.Empty,
                default,
                CancellationPolicy.Default
                    .Select(tier => new CancellationTierResponse(tier.HoursBefore, tier.RefundPercent))
                    .ToArray())
            : ToResponse(policy));
    }

    private static async Task<Results<Ok<CancellationPolicyResponse>, ProblemHttpResult>> SetPolicyAsync(
        Guid venueId,
        SetCancellationPolicyRequest request,
        AppDbContext database,
        CurrentVenue currentVenue,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var invalid = PricingValidation.TryReadTiers(request.Tiers, out var tiers);
        if (invalid is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        var policy = CancellationPolicy.Create(
            venueId, tiers, currentVenue.Require().UserId, timeProvider.GetUtcNow());
        database.CancellationPolicies.Add(policy);
        await database.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(ToResponse(policy));
    }

    /// <summary>The newest published list, which is what a booking made now would be charged.</summary>
    private static Task<PriceList?> InForcePricesAsync(
        AppDbContext database,
        Guid venueId,
        CancellationToken cancellationToken) =>
        database.PriceLists
            .AsNoTracking()
            .Where(list => list.VenueId == venueId)
            .OrderByDescending(list => list.CreatedAt)
            .Include(list => list.Bands)
            .FirstOrDefaultAsync(cancellationToken);

    private static Task<CancellationPolicy?> InForcePolicyAsync(
        AppDbContext database,
        Guid venueId,
        CancellationToken cancellationToken) =>
        database.CancellationPolicies
            .AsNoTracking()
            .Where(policy => policy.VenueId == venueId)
            .OrderByDescending(policy => policy.CreatedAt)
            .Include(policy => policy.Tiers)
            .FirstOrDefaultAsync(cancellationToken);

    private static PriceListResponse ToResponse(PriceList prices) =>
        new(
            prices.Id,
            prices.CreatedAt,
            prices.Bands
                .OrderBy(band => band.Day)
                .ThenBy(band => band.FromHour)
                .Select(band => new PriceBandResponse(
                    band.Day.ToString(), band.FromHour, band.ToHour, band.BahtPerHour))
                .ToArray());

    private static CancellationPolicyResponse ToResponse(CancellationPolicy policy) =>
        new(
            policy.Id,
            policy.CreatedAt,
            policy.Tiers
                .OrderByDescending(tier => tier.HoursBefore)
                .Select(tier => new CancellationTierResponse(tier.HoursBefore, tier.RefundPercent))
                .ToArray());
}
