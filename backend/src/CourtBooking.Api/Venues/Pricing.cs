namespace CourtBooking.Api.Venues;

/// <summary>
/// One published set of prices. A booking keeps a snapshot of what it was charged (PRD BR-05), so a
/// venue changing its prices only ever affects bookings made afterwards — which means versions are
/// added and the newest one is simply the one in force. No effective date: unlike opening hours,
/// which describe a future day, a price applies from the moment it is published.
/// </summary>
public sealed class PriceList
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    public required Guid CreatedByUserId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public List<PriceBand> Bands { get; init; } = [];

    public Venue? Venue { get; init; }

    public static PriceList Create(
        Guid venueId,
        IEnumerable<BandHours> bands,
        Guid byUserId,
        DateTimeOffset at)
    {
        var list = new PriceList
        {
            VenueId = venueId,
            CreatedByUserId = byUserId,
            CreatedAt = at,
        };

        list.Bands.AddRange(bands.Select(band => new PriceBand
        {
            PriceListId = list.Id,
            Day = band.Day,
            FromHour = band.FromHour,
            ToHour = band.ToHour,
            BahtPerHour = band.BahtPerHour,
        }));

        return list;
    }
}

/// <summary>
/// What one stretch of one weekday costs, per court-hour. Bands cover whole hours, the way the
/// opening hours and the availability grid do.
/// </summary>
public sealed class PriceBand
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid PriceListId { get; init; }

    public required DayOfWeek Day { get; init; }

    /// <summary>0–23.</summary>
    public required int FromHour { get; init; }

    /// <summary>1–24, where 24 is midnight at the end of the day.</summary>
    public required int ToHour { get; init; }

    /// <summary>Baht per court-hour, to two decimals (PRD BR-05).</summary>
    public required decimal BahtPerHour { get; init; }

    public PriceList? PriceList { get; init; }
}

/// <summary>One band as the venue asked for it, after validation and before it becomes a row.</summary>
public sealed record BandHours(DayOfWeek Day, int FromHour, int ToHour, decimal BahtPerHour);

/// <summary>
/// What a booker gets back when they cancel. Like prices, a booking snapshots the policy it was
/// made under (PRD BR-05), so versions are added and the newest is the one in force.
/// </summary>
public sealed class CancellationPolicy
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    public required Guid CreatedByUserId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public List<CancellationTier> Tiers { get; init; } = [];

    public Venue? Venue { get; init; }

    /// <summary>What a venue starts with until it says otherwise (PRD US-11, S-11).</summary>
    public static readonly TierTerms[] Default = [new(24, 100)];

    public static CancellationPolicy Create(
        Guid venueId,
        IEnumerable<TierTerms> tiers,
        Guid byUserId,
        DateTimeOffset at)
    {
        var policy = new CancellationPolicy
        {
            VenueId = venueId,
            CreatedByUserId = byUserId,
            CreatedAt = at,
        };

        policy.Tiers.AddRange(tiers.Select(tier => new CancellationTier
        {
            PolicyId = policy.Id,
            HoursBefore = tier.HoursBefore,
            RefundPercent = tier.RefundPercent,
        }));

        return policy;
    }

    /// <summary>
    /// What this policy refunds for a cancellation that many hours before play. The most generous
    /// tier the cancellation qualifies for wins; qualifying for none refunds nothing (PRD US-11).
    /// </summary>
    public int RefundPercentFor(int hoursBeforePlay) =>
        Tiers
            .Where(tier => hoursBeforePlay >= tier.HoursBefore)
            .Select(tier => tier.RefundPercent)
            .DefaultIfEmpty(0)
            .Max();
}

/// <summary>"Cancel this many hours before play and you get this much back."</summary>
public sealed class CancellationTier
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid PolicyId { get; init; }

    /// <summary>Hours before the start of play, 0 or more.</summary>
    public required int HoursBefore { get; init; }

    /// <summary>0–100.</summary>
    public required int RefundPercent { get; init; }

    public CancellationPolicy? Policy { get; init; }
}

public sealed record TierTerms(int HoursBefore, int RefundPercent);
