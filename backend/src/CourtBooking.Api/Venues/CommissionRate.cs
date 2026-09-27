using CourtBooking.Api.Identity;

namespace CourtBooking.Api.Venues;

/// <summary>
/// What the platform charges one venue, from one date (PRD US-21, BR-08).
///
/// Rows are only ever added. A rate is not a setting that gets edited: an invoice run months
/// later has to arrive at the same number the venue was charged at the time, and it works that
/// out by asking which rate was in force on the day each booking was played. Editing the rate
/// in place would silently rewrite what every past month was owed.
///
/// That is also why the date is the day it takes effect rather than the day somebody typed it:
/// the platform agrees a rate with a venue and then enters it, and the agreement is what the
/// bookings are charged against.
/// </summary>
public sealed class CommissionRate
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    /// <summary>The share of what the venue kept, as a percentage (PRD BR-08).</summary>
    public required decimal Percent { get; init; }

    /// <summary>
    /// The first venue day this rate applies to (PRD BR-10). A booking is charged the rate in
    /// force on the day it was played, not on the day it was booked or invoiced.
    /// </summary>
    public required DateOnly EffectiveFrom { get; init; }

    /// <summary>Which admin agreed it, and when they entered it. Not the same as when it starts.</summary>
    public required Guid SetByUserId { get; init; }

    public required DateTimeOffset SetAt { get; init; }

    /// <summary>What the platform wrote about why, for whoever reads this back.</summary>
    public string? Note { get; init; }

    public const int NoteMaxLength = 400;

    public Venue? Venue { get; init; }

    public AppUser? SetBy { get; init; }
}

/// <summary>
/// Which rate a booking is charged at, and what may be entered as one (PRD US-21, BR-08). One
/// place, because the number an invoice is built from and the number a screen shows have to be
/// the same number.
/// </summary>
public static class Commission
{
    /// <summary>
    /// The rate in force on one of the venue's days: the newest one that had already started.
    /// Null where the platform had not agreed a rate with this venue yet, which is not nought —
    /// nought is a rate somebody chose, and null is a question nobody has answered. An invoice
    /// run must not quietly charge a venue nothing because of a gap in its own records.
    /// </summary>
    public static CommissionRate? InForceOn(IEnumerable<CommissionRate> rates, DateOnly day) =>
        rates
            .Where(rate => rate.EffectiveFrom <= day)
            .OrderByDescending(rate => rate.EffectiveFrom)
            // Two rates starting on the same day is the platform correcting itself; the later
            // entry is the correction.
            .ThenByDescending(rate => rate.SetAt)
            .ThenByDescending(rate => rate.Id)
            .FirstOrDefault();

    /// <summary>
    /// What one booking adds to a month's commission: the rate's share of what the venue kept
    /// (PRD BR-08), rounded to satang. The base is the amount including VAT, which PRD BR-08
    /// states as an assumption still to be confirmed with an accountant (S-12).
    /// </summary>
    public static decimal On(decimal keptBaht, decimal percent) =>
        decimal.Round(keptBaht * percent / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>The most the platform may charge. A rate above this is a typing mistake.</summary>
    public const decimal MostPercent = 100m;

    /// <summary>
    /// Whether a number somebody typed could be a rate: a share of nothing to all of it, in
    /// hundredths of a percent — which is as fine as any rate is ever agreed.
    /// </summary>
    public static bool IsARate(decimal percent) =>
        percent >= 0m
        && percent <= MostPercent
        && decimal.Round(percent, 2, MidpointRounding.AwayFromZero) == percent;
}
