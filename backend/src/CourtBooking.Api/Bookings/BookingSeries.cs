using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Bookings;

/// <summary>Whether a standing arrangement is still making bookings (PRD US-30).</summary>
public enum SeriesState
{
    /// <summary>Still running: the weeks ahead are filled in as the booking window rolls forward.</summary>
    Running = 1,

    /// <summary>
    /// Over. Nothing more is made for it — either the venue stopped it, or the arrangement that
    /// took over from next week replaced it. The row stays, with when and why (PRD US-30).
    /// </summary>
    Ended = 2,
}

/// <summary>
/// A group that comes at the same hour every week (PRD US-30).
///
/// It is not a booking. It is the arrangement a venue has with a group, written down once, and the
/// bookings it makes are ordinary counter bookings — same channel, same write, same money. Nothing
/// here is a second way to keep a court, because a second way is a way the constraint that stops
/// double booking cannot see (PRD BR-04).
///
/// What it deliberately does not do is reach further ahead than anybody can book. The weeks appear
/// as the booking window rolls (PRD S-04), each one priced on the day it is made and not on the day
/// the arrangement was agreed (BR-05) — a group that started in March pays April's evening rate in
/// April, the same as anybody booking April by hand.
/// </summary>
public sealed class BookingSeries
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    /// <summary>The court they play on. One court, because a group plays together.</summary>
    public required Guid CourtId { get; init; }

    /// <summary>The day of the week they come, read in the venue's own week (PRD BR-10).</summary>
    public required DayOfWeek Day { get; init; }

    /// <summary>The hour they start, as an hour of that day.</summary>
    public required int FromHour { get; init; }

    /// <summary>The hour they finish, so 20 with <see cref="FromHour"/> 18 is two hours.</summary>
    public required int UntilHour { get; init; }

    /// <summary>Who the group is, in the words they gave — as at the counter (PRD US-13).</summary>
    public required string CustomerName { get; init; }

    /// <summary>Optional, and only ever used by the venue to reach them (PRD US-13).</summary>
    public string? CustomerPhone { get; init; }

    /// <summary>The first date it may cover. Occurrences fall on or after it.</summary>
    public required DateOnly StartsOn { get; init; }

    /// <summary>The last date it may cover, inclusive. Null means it runs until somebody stops it.</summary>
    public DateOnly? UntilOn { get; init; }

    public SeriesState State { get; set; } = SeriesState.Running;

    public required DateTimeOffset CreatedAt { get; init; }

    public required Guid CreatedByUserId { get; init; }

    public DateTimeOffset? EndedAt { get; set; }

    public Guid? EndedByUserId { get; set; }

    /// <summary>Why it ended, in the venue's own words. Kept rather than deleted (PRD US-30).</summary>
    public string? EndReason { get; set; }

    /// <summary>
    /// The arrangement this one took over from, when the venue changed the hour or the court from
    /// next week. The old one ends and this one starts, so the weeks already played keep the terms
    /// they were played on and the thread between them is still readable (PRD US-30).
    /// </summary>
    public Guid? ReplacedSeriesId { get; init; }

    public int Hours => UntilHour - FromHour;

    public Venue? Venue { get; init; }

    public Court? Court { get; init; }

    public BookingSeries? Replaced { get; init; }
}

/// <summary>
/// A week the arrangement could not have, and why (PRD US-30).
///
/// The venue is told rather than quietly given nothing: an hour somebody else already has, or a
/// court shut that afternoon, is a week for the venue to sort out — by moving the group, or by
/// ringing them. The row is append-only, so a week that was missed stays missed even once the hour
/// comes free again: the next sweep does not creep in behind the venue and take an hour it has
/// already been told about and may have promised to somebody else.
/// </summary>
public sealed class SeriesMiss
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid SeriesId { get; init; }

    /// <summary>The date it would have been, in the venue's week.</summary>
    public required DateOnly Date { get; init; }

    /// <summary>The refusal code, as the booking write gave it. The screen says it in words (US-23).</summary>
    public required string Refusal { get; init; }

    public required DateTimeOffset NoticedAt { get; init; }

    public BookingSeries? Series { get; init; }
}

/// <summary>
/// What a standing arrangement will accept, and which dates it falls on, in one place — so the
/// screen that asks, the endpoint that writes and the job that fills the weeks in all agree about
/// it (PRD US-30).
/// </summary>
public static class Series
{
    /// <summary>
    /// How far ahead the weeks are filled in. The same window anybody booking by hand has, because
    /// an arrangement reaching further would be holding hours nobody else could even ask for.
    /// </summary>
    public static int FillWithinDays => Availability.BookableDaysAhead;

    public const int RefusalMaxLength = 80;

    /// <summary>
    /// How many of the arrangements that have stopped a venue is shown. They are kept for ever —
    /// a group that left explains a quiet Tuesday — but the list somebody works from is the one
    /// that is still running, and a year of stopped ones underneath it buries that.
    /// </summary>
    public const int EndedShown = 10;

    /// <summary>
    /// Why this arrangement cannot be written down, or null. It says nothing about whether the
    /// hours are free: that is the floor's answer, week by week, and it changes.
    /// </summary>
    public static string? Refusal(
        int fromHour,
        int untilHour,
        DateOnly startsOn,
        DateOnly? untilOn,
        DateOnly today)
    {
        if (fromHour < 0 || untilHour > 24 || fromHour >= untilHour)
        {
            return SeriesErrorCodes.NotAWindow;
        }

        // Starting in the past would mean asking the floor for weeks already played.
        if (startsOn < today)
        {
            return SeriesErrorCodes.StartsInThePast;
        }

        if (untilOn is { } last && last < startsOn)
        {
            return SeriesErrorCodes.EndsBeforeItStarts;
        }

        return null;
    }

    /// <summary>
    /// The dates this arrangement falls on inside a range, in order. Empty where the range falls
    /// outside it altogether, which is the ordinary answer once a week has been filled in.
    /// </summary>
    public static IEnumerable<DateOnly> Dates(BookingSeries series, DateOnly from, DateOnly to)
    {
        var last = series.UntilOn is { } until && until < to ? until : to;
        var first = series.StartsOn > from ? series.StartsOn : from;

        for (var date = FirstOnOrAfter(series.Day, first); date <= last; date = date.AddDays(7))
        {
            yield return date;
        }
    }

    /// <summary>The first date on or after <paramref name="from"/> falling on that weekday.</summary>
    public static DateOnly FirstOnOrAfter(DayOfWeek day, DateOnly from) =>
        from.AddDays(((int)day - (int)from.DayOfWeek + 7) % 7);
}

public static class SeriesErrorCodes
{
    /// <summary>The hours are not a window: they run backwards, or off the end of the day.</summary>
    public const string NotAWindow = "series.not_a_window";

    public const string StartsInThePast = "series.starts_in_the_past";

    /// <summary>
    /// A change cannot begin further ahead than the weeks can be booked. The arrangement it takes
    /// over from stops when the change is agreed, so a handover beyond the booking window would
    /// leave the weeks in between with nobody making them (PRD US-30, S-04).
    /// </summary>
    public const string ChangeTooFarAhead = "series.change_too_far_ahead";

    /// <summary>
    /// This venue already has a group on that court, that day, at those hours. A second one could
    /// never have a single week of it, so it is refused rather than left to miss every week.
    /// </summary>
    public const string AlreadyStanding = "series.already_standing";
    public const string EndsBeforeItStarts = "series.ends_before_it_starts";

    /// <summary>No court of this venue has that id.</summary>
    public const string CourtUnknown = "series.court_unknown";

    /// <summary>Not one of the seven names, spelled as it is spelled.</summary>
    public const string DayUnknown = "series.day_unknown";

    public const string NotFound = "series.not_found";

    /// <summary>It has already been stopped, so there is nothing left to stop.</summary>
    public const string AlreadyEnded = "series.already_ended";
}
