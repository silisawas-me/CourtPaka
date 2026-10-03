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
    /// One band per hour of every day is as fine-grained as a price can be, so anything past that
    /// is a mistake or a mishap rather than a price list.
    /// </summary>
    public const int MaxBands = 7 * 24;

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

        if (bands.Count > MaxBands)
        {
            return PricingErrorCodes.TooManyBands;
        }

        foreach (var band in bands)
        {
            if (CourtValidation.ReadDay(band.Day) is not DayOfWeek day)
            {
                return CourtErrorCodes.InvalidDay;
            }

            // A band always has hours; only a weekday may be closed.
            var invalidHours = CourtValidation.ValidateBandHours(band.FromHour, band.ToHour);
            if (invalidHours is not null)
            {
                return invalidHours;
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
    /// hour it cannot charge for (PRD US-11).
    ///
    /// Every week the venue has committed to is checked, not only the one running today: a price
    /// list has no end date, so it governs the weeks already published for future dates too.
    /// </summary>
    public static string? CoversOpeningHours(
        IReadOnlyCollection<BandHours> bands,
        IReadOnlyCollection<OpeningHoursSchedule> weeks)
    {
        if (weeks.Count == 0)
        {
            return PricingErrorCodes.NoOpeningHours;
        }

        return weeks.Select(week => EveryOpenHourPriced(bands, week)).FirstOrDefault(fault => fault is not null);
    }

    /// <summary>
    /// The same rule read from the other side, because either write can break it: a week that opens
    /// an hour the prices do not cover would put an hour in the grid with nothing to charge for it.
    /// A venue that has not priced anything yet is publishing its hours first, which is the normal
    /// order and is allowed.
    /// </summary>
    public static string? PricedByExistingBands(
        OpeningHoursSchedule week,
        IReadOnlyCollection<BandHours> bands) =>
        bands.Count == 0 ? null : EveryOpenHourPriced(bands, week);

    private static string? EveryOpenHourPriced(
        IReadOnlyCollection<BandHours> bands,
        OpeningHoursSchedule week)
    {
        foreach (var day in week.Days)
        {
            if (day.OpensHour is not int opens || day.ClosesHour is not int closes)
            {
                continue;
            }

            var priced = bands.Where(band => band.Day == day.Day).ToList();
            for (var hour = opens; hour < closes; hour++)
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
        bands.GroupBy(band => band.Day).Any(day =>
        {
            var sorted = day.OrderBy(band => band.FromHour).ToList();
            return sorted.Zip(sorted.Skip(1)).Any(pair => pair.First.ToHour > pair.Second.FromHour);
        });
}
