using CourtBooking.Api.Identity;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Bookings;

/// <summary>Why hours moved in or out of a package (PRD US-31).</summary>
public enum PackageMove
{
    /// <summary>The hours the customer bought.</summary>
    Sold = 1,

    /// <summary>Hours spent on a booking.</summary>
    Used = 2,

    /// <summary>Hours handed back when a booking they paid for was cancelled (BR-06).</summary>
    GivenBack = 3,

    /// <summary>Whatever was left on the day it ran out (S-28).</summary>
    Expired = 4,
}

/// <summary>
/// A kind of package a venue sells: so many hours, for so much, good for so long (PRD US-31).
///
/// It is a price on a board, and like every other price in this system it is never edited — a
/// venue changes its offer by withdrawing this one and putting up another, so the packages
/// already sold from it still point at the terms they were sold on (BR-05).
/// </summary>
public sealed class PackageType
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    /// <summary>What the venue calls it on the board, in their own words.</summary>
    public required string Name { get; init; }

    public required int Hours { get; init; }

    public required decimal PriceBaht { get; init; }

    /// <summary>How long the hours last, counted from the day they are sold (⚠️ S-28).</summary>
    public required int ValidForDays { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required Guid CreatedByUserId { get; init; }

    /// <summary>
    /// When it came off the board, if it has. One way: a withdrawn offer is not put back, it is
    /// replaced — which is what keeps a sold package's terms readable.
    /// </summary>
    public DateTimeOffset? WithdrawnAt { get; set; }

    public Guid? WithdrawnByUserId { get; set; }

    public const int NameMaxLength = 100;

    public Venue? Venue { get; init; }
}

/// <summary>
/// One package somebody bought (PRD US-31).
///
/// The hours it has left are not kept here. They are the sum of <see cref="PackageEntry"/> rows,
/// because a balance that is written down is a balance that can disagree with the movements that
/// made it — and this one is the venue's word to a customer about something they paid for.
///
/// What it cost and how many hours it was are copied in from the offer, so the venue changing its
/// board afterwards changes nothing about a package already sold (BR-05).
/// </summary>
public sealed class HourPackage
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    public required Guid PackageTypeId { get; init; }

    /// <summary>Who bought it, in the words they gave — as at the counter (PRD US-13).</summary>
    public required string CustomerName { get; init; }

    public string? CustomerPhone { get; init; }

    public required int HoursSold { get; init; }

    public required decimal PriceBaht { get; init; }

    /// <summary>The last day the hours can be spent, in the venue's own week (BR-10).</summary>
    public required DateOnly ExpiresOn { get; init; }

    public required DateTimeOffset SoldAt { get; init; }

    public required Guid SoldByUserId { get; init; }

    /// <summary>
    /// When the hours left on it were written off, if they have been. Claimed before the row that
    /// writes them off, so a second sweep finds nothing left to do rather than doing it twice.
    /// </summary>
    public DateTimeOffset? ExpiredAt { get; set; }

    public Venue? Venue { get; init; }

    public PackageType? Type { get; init; }

    public List<PackageEntry> Entries { get; init; } = [];
}

/// <summary>
/// One movement of hours, in or out (PRD US-31). Rows are only ever added — the database refuses
/// anything else — so what a package has left is always the sum of what happened to it, and the
/// answer to "where did my hours go" is a list rather than a number somebody has to be trusted on.
/// </summary>
public sealed class PackageEntry
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid PackageId { get; init; }

    /// <summary>Positive going in, negative going out. Never zero.</summary>
    public required int Hours { get; init; }

    public required PackageMove Move { get; init; }

    /// <summary>The booking the hours went to or came back from, where there is one.</summary>
    public Guid? BookingId { get; init; }

    public required DateTimeOffset At { get; init; }

    /// <summary>Who did it. Null where the clock did — hours running out (PRD 6.1's rule).</summary>
    public Guid? ByUserId { get; init; }

    public HourPackage? Package { get; init; }

    public Booking? Booking { get; init; }

    public AppUser? By { get; init; }
}

/// <summary>
/// What a venue's packages will accept and what they are worth, in one place — so the screen that
/// sells one, the endpoint that writes it and the job that runs them out all agree (PRD US-31).
/// </summary>
public static class Packages
{
    /// <summary>The most hours one package may hold. A season, not a lifetime.</summary>
    public const int MostHours = 200;

    /// <summary>The longest an offer may stay good for: two years.</summary>
    public const int MostDays = 730;

    /// <summary>How far ahead a venue is warned that somebody's hours are about to run out.</summary>
    public const int RunningOutWithinDays = 14;

    /// <summary>
    /// How many of the packages that are finished with — spent to nothing, or run out — a venue is
    /// shown. Every package is kept for ever, and a year of them under the ones still being spent
    /// buries the list somebody is working from.
    /// </summary>
    public const int FinishedShown = 10;

    /// <summary>Why this offer cannot go on the board, or null.</summary>
    public static string? Refusal(string? name, int hours, decimal priceBaht, int validForDays)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > PackageType.NameMaxLength)
        {
            return PackageErrorCodes.InvalidName;
        }

        if (hours < 1 || hours > MostHours)
        {
            return PackageErrorCodes.InvalidHours;
        }

        // Nothing free: a package with no price is hours given away, which is a decision, not a
        // package, and it would divide by nothing below.
        if (priceBaht <= 0m || priceBaht != decimal.Round(priceBaht, 2))
        {
            return PackageErrorCodes.InvalidPrice;
        }

        return validForDays < 1 || validForDays > MostDays
            ? PackageErrorCodes.InvalidValidity
            : null;
    }

    /// <summary>
    /// What one hour of this package cost. It is what the customer actually paid for an hour, and
    /// it is what an hour is worth to the venue when it is spent — not the price on the board that
    /// day, which is what they chose not to pay (PRD US-31, ⚠️ S-27).
    /// </summary>
    public static decimal PerHour(decimal priceBaht, int hours) =>
        decimal.Round(priceBaht / hours, 2, MidpointRounding.AwayFromZero);

    /// <summary>What a run of hours off this package is worth.</summary>
    public static decimal Worth(decimal priceBaht, int hoursSold, int hours) =>
        decimal.Round(PerHour(priceBaht, hoursSold) * hours, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// How many hours come back when a booking they paid for is cancelled (PRD US-31, BR-06). The
    /// share is the cancellation terms' own, and it is rounded up: a venue keeping four fifths of
    /// an hour from somebody is not a thing anybody can hand over, and the customer gets the
    /// benefit of it.
    /// </summary>
    public static int HoursBack(int hoursUsed, int refundPercent) =>
        (int)Math.Ceiling(hoursUsed * refundPercent / 100m);
}

public static class PackageErrorCodes
{
    public const string InvalidName = "package.invalid_name";
    public const string InvalidHours = "package.invalid_hours";
    public const string InvalidPrice = "package.invalid_price";
    public const string InvalidValidity = "package.invalid_validity";

    /// <summary>No offer of this venue has that id, or it has been taken off the board.</summary>
    public const string TypeUnknown = "package.type_unknown";

    public const string TypeAlreadyWithdrawn = "package.type_already_withdrawn";

    /// <summary>No package of this venue has that id.</summary>
    public const string NotFound = "package.not_found";

    /// <summary>The hours ran out, or the day they were good until has passed.</summary>
    public const string RunOut = "package.run_out";

    /// <summary>Fewer hours left than the booking needs. A package pays for a booking whole.</summary>
    public const string NotEnoughHours = "package.not_enough_hours";

    /// <summary>
    /// Hours pay instead of money, not alongside it. A booking somebody has already put money
    /// against is settled the way it was started.
    /// </summary>
    public const string AlreadyPaidFor = "package.already_paid_for";

    /// <summary>This booking is not one a package can pay for (PRD US-31).</summary>
    public const string CannotPayForThat = "package.cannot_pay_for_that";
}
