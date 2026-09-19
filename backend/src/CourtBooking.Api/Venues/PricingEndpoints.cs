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

        // Checked against every week the venue has committed to from today on, not only the one
        // running today: these prices govern those weeks too, and a venue whose hours start
        // tomorrow must still be able to price them (PRD US-11).
        var schedules = await CourtEndpoints.SchedulesAsync(database, venueId, cancellationToken);
        var committed = VenueTimeline
            .CurrentAndUpcoming(schedules, PlatformRequirements.BangkokToday(timeProvider))
            .ToList();

        var uncovered = PricingValidation.CoversOpeningHours(bands, committed);
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
        // A venue gets its default policy when it is created, and the migration gave one to every
        // venue that predates the feature. A venue that still has none — one written by some other
        // path — reads as the terms it would have started with rather than as a 500.
        var policy = await InForcePolicyAsync(database, venueId, cancellationToken);
        return TypedResults.Ok(policy is null ? DefaultTerms() : ToResponse(policy));
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
    internal static Task<PriceList?> InForcePricesAsync(
        AppDbContext database,
        Guid venueId,
        CancellationToken cancellationToken) =>
        database.PriceLists
            .AsNoTracking()
            .Where(list => list.VenueId == venueId)
            // The id breaks a tie: two publishes can land in the same timestamp, and a clock that
            // steps backwards must not resurrect an older list. UUIDv7 is ordered by creation.
            .OrderByDescending(list => list.CreatedAt)
            .ThenByDescending(list => list.Id)
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
            .ThenByDescending(policy => policy.Id)
            .Include(policy => policy.Tiers)
            .FirstOrDefaultAsync(cancellationToken);

    private static CancellationPolicyResponse DefaultTerms() =>
        new(
            Guid.Empty,
            default,
            CancellationPolicy.Default
                .Select(tier => new CancellationTierResponse(tier.HoursBefore, tier.RefundPercent))
                .ToArray());

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
