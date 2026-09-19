namespace CourtBooking.Api.Venues;

/// <summary>
/// What a set of prices and a cancellation policy have to satisfy. The rules are here rather than
/// in the endpoints so they can be read, and tested, on their own (PRD US-11).
/// </summary>
public static class PricingValidation
{
    public const decimal MaxBahtPerHour = 100_000m;
    public const int MaxTiers = 3;

    /// <summary>
    /// Turns the submitted bands into the rows to store, or names the first thing wrong with them.
    /// Two bands may not cover the same hour, because then the hour has two prices.
    /// </summary>
    public static string? TryReadBands(
        IReadOnlyCollection<PriceBandRequest>? bands,
        out List<BandHours> read)
    {
        read = [];
        if (bands is null || bands.Count == 0)
        {
            return PricingErrorCodes.NoBands;
        }

        foreach (var band in bands)
        {
            if (!Enum.TryParse<DayOfWeek>(band.Day, ignoreCase: true, out var day) || !Enum.IsDefined(day))
            {
                return CourtErrorCodes.InvalidDay;
            }

            if (band.FromHour is < CourtValidation.EarliestOpeningHour or > CourtValidation.LatestOpeningHour
                || band.ToHour is < CourtValidation.EarliestClosingHour or > CourtValidation.LatestClosingHour
                || band.ToHour <= band.FromHour)
            {
                return CourtErrorCodes.InvalidHours;
            }

            if (band.BahtPerHour <= 0 || band.BahtPerHour > MaxBahtPerHour
                || decimal.Round(band.BahtPerHour, 2) != band.BahtPerHour)
            {
                return PricingErrorCodes.InvalidPrice;
            }

            read.Add(new BandHours(day, band.FromHour, band.ToHour, band.BahtPerHour));
        }

        return Overlapping(read) ? PricingErrorCodes.OverlappingBands : null;
    }

    /// <summary>
    /// Every hour the venue is open has to have a price, or the availability grid would offer an
    /// hour it cannot charge for. The week in force on the day the prices are published is what
    /// they are checked against (PRD US-11).
    /// </summary>
    public static string? CoversOpeningHours(
        IReadOnlyCollection<BandHours> bands,
        OpeningHoursSchedule? openingHours)
    {
        if (openingHours is null)
        {
            return PricingErrorCodes.NoOpeningHours;
        }

        foreach (var day in openingHours.Days.Where(day => day.OpensHour is not null))
        {
            var priced = bands.Where(band => band.Day == day.Day).ToList();
            for (var hour = day.OpensHour!.Value; hour < day.ClosesHour!.Value; hour++)
            {
                if (!priced.Any(band => band.FromHour <= hour && hour < band.ToHour))
                {
                    return PricingErrorCodes.HourWithoutPrice;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Turns the submitted tiers into the rows to store, or names the first thing wrong with them.
    /// Cancelling earlier can never get you less back, which is the rule that makes the tiers read
    /// as a ladder rather than a lottery (PRD US-11).
    /// </summary>
    public static string? TryReadTiers(
        IReadOnlyCollection<CancellationTierRequest>? tiers,
        out List<TierTerms> read)
    {
        read = [];
        if (tiers is null || tiers.Count == 0)
        {
            return PricingErrorCodes.NoTiers;
        }

        if (tiers.Count > MaxTiers)
        {
            return PricingErrorCodes.TooManyTiers;
        }

        foreach (var tier in tiers)
        {
            if (tier.HoursBefore < 0 || tier.RefundPercent is < 0 or > 100)
            {
                return PricingErrorCodes.InvalidTier;
            }

            read.Add(new TierTerms(tier.HoursBefore, tier.RefundPercent));
        }

        if (read.Select(tier => tier.HoursBefore).Distinct().Count() != read.Count)
        {
            return PricingErrorCodes.DuplicateTier;
        }

        var ladder = read.OrderBy(tier => tier.HoursBefore).ToList();
        return ladder.Zip(ladder.Skip(1)).Any(pair => pair.Second.RefundPercent < pair.First.RefundPercent)
            ? PricingErrorCodes.TiersNotInOrder
            : null;
    }

    private static bool Overlapping(IReadOnlyCollection<BandHours> bands) =>
        bands
            .GroupBy(band => band.Day)
            .Any(day => day
                .OrderBy(band => band.FromHour)
                .Zip(day.OrderBy(band => band.FromHour).Skip(1))
                .Any(pair => pair.First.ToHour > pair.Second.FromHour));
}
