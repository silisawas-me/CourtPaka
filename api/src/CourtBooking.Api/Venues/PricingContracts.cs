namespace CourtBooking.Api.Venues;

public sealed record PriceBandRequest(string Day, int FromHour, int ToHour, decimal BahtPerHour);

public sealed record SetPricesRequest(PriceBandRequest[] Bands);

public sealed record PriceBandResponse(string Day, int FromHour, int ToHour, decimal BahtPerHour);

/// <summary>
/// The prices in force. A booking snapshots what it was charged, so an older list is history rather
/// than something a screen needs (PRD BR-05).
/// </summary>
public sealed record PriceListResponse(Guid Id, DateTimeOffset CreatedAt, PriceBandResponse[] Bands);

public sealed record CancellationTierRequest(int HoursBefore, int RefundPercent);

public sealed record SetCancellationPolicyRequest(CancellationTierRequest[] Tiers);

public sealed record CancellationTierResponse(int HoursBefore, int RefundPercent);

public sealed record CancellationPolicyResponse(
    Guid Id,
    DateTimeOffset CreatedAt,
    CancellationTierResponse[] Tiers);

public static class PricingErrorCodes
{
    public const string NoBands = "pricing.no_bands";
    public const string TooManyBands = "pricing.too_many_bands";
    public const string InvalidPrice = "pricing.invalid_price";
    public const string OverlappingBands = "pricing.overlapping_bands";
    public const string HourWithoutPrice = "pricing.hour_without_price";
    public const string NoOpeningHours = "pricing.no_opening_hours";
    public const string NoTiers = "pricing.no_tiers";
    public const string TooManyTiers = "pricing.too_many_tiers";
    public const string InvalidTier = "pricing.invalid_tier";
    public const string DuplicateTier = "pricing.duplicate_tier";
    public const string TiersNotInOrder = "pricing.tiers_not_in_order";
}
