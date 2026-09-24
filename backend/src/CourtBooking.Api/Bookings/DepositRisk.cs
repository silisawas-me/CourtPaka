using CourtBooking.Api.Data;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>Why a booking was asked for what it was asked for (PRD US-28).</summary>
public enum DepositReason
{
    /// <summary>What this venue asks everybody for.</summary>
    VenueTerms = 1,

    /// <summary>This booker has not turned up often enough for the venue to ask for more.</summary>
    SomeNoShows = 2,

    /// <summary>Often enough, and these are hours the venue cannot afford to lose.</summary>
    ManyNoShowsAtPeak = 3,
}

/// <summary>
/// How much a venue asks of somebody by what happened last time (PRD US-28).
///
/// The rule only ever asks for more, never less: a venue's own terms are the floor, and a booker
/// with nothing against them is asked exactly what everybody else is asked. That is what makes it
/// safe to leave on — a venue asking for the whole price already asks for the most there is.
///
/// Counted at this venue and nowhere else. A venue can only answer for hours it sold, and one
/// venue's opinion of a booker is not another's to read (PDPA); the thresholds are the venue's
/// too, because a court by an office and a court in a suburb do not lose the same evening.
/// </summary>
public static class DepositRisk
{
    public const int DefaultLookbackDays = 60;
    public const int DefaultHalfAt = 2;
    public const int DefaultFullAt = 3;

    /// <summary>Half of a price, which is what the middle tier asks for at least.</summary>
    public const int Half = 50;

    /// <summary>The most a venue may look back. A year of a booker's misses is not a habit.</summary>
    public const int MaxLookbackDays = 365;

    /// <summary>
    /// The most misses a venue may wait for. Past this the rule is one that never applies, which
    /// is a setting that looks on and is not — turning it off says that, and says it plainly.
    /// </summary>
    public const int MaxMisses = 50;

    /// <summary>Whether a set of thresholds is one a venue may ask for.</summary>
    public static bool AreThresholds(int lookbackDays, int halfAt, int fullAt) =>
        lookbackDays is > 0 and <= MaxLookbackDays
        && halfAt is > 0 and <= MaxMisses
        && fullAt >= halfAt
        && fullAt <= MaxMisses;

    /// <summary>
    /// How many times this booker has been recorded as not turning up here, inside the venue's
    /// own window (PRD US-28). By the hours that went unused rather than by when the venue
    /// pressed the button: a miss belongs to the evening it happened on.
    /// </summary>
    /// <param name="rule">
    /// The venue's own. A venue that turned the rule off is not asked the question at all — what
    /// it said is that it would rather not keep a count of who let it down.
    /// </param>
    public static Task<int> MissesAsync(
        AppDbContext database,
        Guid venueId,
        Guid bookerUserId,
        VenueRiskRule rule,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!rule.On)
        {
            return Task.FromResult(0);
        }

        var since = now.AddDays(-rule.LookbackDays);

        return database.Bookings
            .AsNoTracking()
            .CountAsync(
                booking => booking.VenueId == venueId
                    && booking.BookerUserId == bookerUserId
                    && booking.Status == BookingStatus.NoShow
                    && booking.Slots.Any(slot => slot.StartsAt >= since),
                cancellationToken);
    }

    /// <summary>Whether any of these hours is one the venue said it cannot afford to lose.</summary>
    public static bool TouchesPeak(VenueRiskRule rule, IEnumerable<SlotPrice> slots) =>
        slots.Any(slot => rule.IsPeak(PlatformRequirements.BangkokDateAndHour(slot.StartsAt).Hour));

    /// <summary>
    /// What this booking has to be held on, and why. The venue's own share unless somebody has
    /// left hours unused often enough — and the whole price where those hours are the ones it
    /// cannot sell twice.
    /// </summary>
    public static (int Percent, DepositReason Reason) Asks(
        VenueRiskRule rule,
        int venuePercent,
        int noShows,
        bool atPeak)
    {
        if (!rule.On)
        {
            return (venuePercent, DepositReason.VenueTerms);
        }

        if (noShows >= rule.FullAt && atPeak)
        {
            return Raised(venuePercent, Deposit.Everything, DepositReason.ManyNoShowsAtPeak);
        }

        if (noShows >= rule.HalfAt)
        {
            return Raised(venuePercent, Half, DepositReason.SomeNoShows);
        }

        return (venuePercent, DepositReason.VenueTerms);
    }

    /// <summary>
    /// The tier's floor against the venue's own share, and the reason only where the floor is
    /// actually higher.
    ///
    /// Somebody charged exactly what the person beside them is charged has not been asked for
    /// more, and must not be told they have: a venue asking for the whole price already asks for
    /// the most there is, which is every venue until one says otherwise — and being told you are
    /// paying for a history when you are paying the ordinary amount is both false and a thing
    /// said about somebody for no reason.
    /// </summary>
    private static (int Percent, DepositReason Reason) Raised(
        int venuePercent,
        int floor,
        DepositReason reason) =>
        floor > venuePercent ? (floor, reason) : (venuePercent, DepositReason.VenueTerms);
}

/// <summary>
/// What a venue counts as too often, and which hours it will not lose (PRD US-28). Kept beside
/// the venue rather than versioned: a booking snapshots what it was asked for and why, so the
/// rule is only ever read forwards.
/// </summary>
public sealed class VenueRiskRule
{
    /// <summary>
    /// Whether the rule applies at all. On by default and harmless there: a venue asking for the
    /// whole price cannot be asked for more, and the rule never asks for less.
    /// </summary>
    public bool On { get; set; } = true;

    /// <summary>How far back a miss still counts.</summary>
    public int LookbackDays { get; set; } = DepositRisk.DefaultLookbackDays;

    /// <summary>The number of misses at which half the price has to arrive.</summary>
    public int HalfAt { get; set; } = DepositRisk.DefaultHalfAt;

    /// <summary>The number at which the whole price has to, for the hours below.</summary>
    public int FullAt { get; set; } = DepositRisk.DefaultFullAt;

    /// <summary>
    /// The hours a venue cannot afford to lose, as a half-open range in venue time. Null means
    /// the venue has not said, and then no hour is one — the top tier asks for half like the one
    /// below it rather than guessing which evening matters.
    /// </summary>
    public int? PeakFromHour { get; set; }

    public int? PeakUntilHour { get; set; }

    /// <summary>Whether an hour of the day is one of the ones the venue named.</summary>
    public bool IsPeak(int hour) =>
        PeakFromHour is { } from && PeakUntilHour is { } until && hour >= from && hour < until;

    /// <summary>Whether a set of peak hours is a range of the day, or nothing at all.</summary>
    public static bool IsAWindow(int? fromHour, int? untilHour) =>
        (fromHour is null && untilHour is null)
        || (fromHour is >= 0 and <= 23
            && untilHour is >= 1 and <= 24
            && fromHour < untilHour);
}
