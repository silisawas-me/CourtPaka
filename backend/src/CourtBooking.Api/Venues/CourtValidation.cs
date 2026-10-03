namespace CourtBooking.Api.Venues;

/// <summary>
/// Everything a court or a week of opening hours has to satisfy before it reaches the database.
/// Kept apart from the endpoints so the rules can be read, and tested, on their own.
/// </summary>
public static class CourtValidation
{
    public const int NameMaxLength = 50;
    public const int PositionMax = 500;
    public const int EarliestOpeningHour = 0;
    public const int LatestOpeningHour = 23;
    public const int EarliestClosingHour = 1;

    /// <summary>Midnight at the end of the day, so a closure that ends at 00:00 is expressible.</summary>
    public const int Midnight = 24;

    /// <summary>
    /// A venue may stay open past midnight, until 06:00 (thai-fit T4): Friday closing at 26 is
    /// 02:00 Saturday, and those hours are Friday's (<see cref="Localization.VenueClock"/>).
    /// </summary>
    public const int LatestClosingHour = Midnight + Localization.VenueClock.LatestDayStart;

    public static string? ValidateName(string? name) =>
        VenueValidation.ValidateText(name, NameMaxLength, CourtErrorCodes.InvalidCourtName);

    public static string? ValidatePosition(int position) =>
        position is < 0 or > PositionMax ? CourtErrorCodes.InvalidPosition : null;

    /// <summary>
    /// A closure has to be a stretch of whole hours that has not already passed (PRD US-11). The
    /// past is refused rather than accepted quietly: shutting a court for last Tuesday changes
    /// nothing anybody can book and makes the report of why that evening was empty a lie.
    /// </summary>
    public static string? ValidateClosure(
        DateOnly startsOn,
        int startHour,
        DateOnly endsOn,
        int endHour,
        DateOnly today)
    {
        if (startHour is < EarliestOpeningHour or > LatestOpeningHour
            || endHour is < EarliestClosingHour or > Midnight)
        {
            return CourtErrorCodes.InvalidClosureRange;
        }

        var starts = startsOn.ToDateTime(TimeOnly.MinValue).AddHours(startHour);
        var ends = endsOn.ToDateTime(TimeOnly.MinValue).AddHours(endHour);

        if (ends <= starts)
        {
            return CourtErrorCodes.InvalidClosureRange;
        }

        // Measured by the day it ends: a closure running from last week into next week is still
        // shutting hours somebody could otherwise book.
        return endsOn < today ? CourtErrorCodes.InvalidClosureRange : null;
    }

    public static string? ValidateClosureReason(string? reason) =>
        VenueValidation.ValidateText(
            reason, CourtClosure.ReasonMaxLength, CourtErrorCodes.InvalidClosureReason);

    /// <summary>A setting may start today or later; what it said in the past cannot be rewritten.</summary>
    public static string? ValidateEffectiveFrom(DateOnly effectiveFrom, DateOnly today) =>
        effectiveFrom < today ? CourtErrorCodes.EffectiveDateInThePast : null;

    /// <summary>A weekday as it travels on the wire, or null when it is not one.</summary>
    internal static DayOfWeek? ReadDay(string? name) =>
        Enum.TryParse<DayOfWeek>(name, ignoreCase: true, out var day) && Enum.IsDefined(day) ? day : null;

    /// <summary>
    /// The hour grammar every setting shares: open on an hour from 0 to 23, close on one from 1 to
    /// 24, and close after opening. Both null means closed, which only a weekday can be.
    /// </summary>
    internal static string? ValidateHours(int? opens, int? closes, bool closedAllowed)
    {
        if (opens is null && closes is null)
        {
            return closedAllowed ? null : CourtErrorCodes.InvalidHours;
        }

        return opens is null or < EarliestOpeningHour or > LatestOpeningHour
               || closes is null or < EarliestClosingHour or > LatestClosingHour
               || closes <= opens
            ? CourtErrorCodes.InvalidHours
            : null;
    }

    /// <summary>
    /// Turns the seven submitted days into the week to store, or names the first thing wrong with
    /// them. A week has to be complete: leaving a day out would otherwise read as "closed" by
    /// accident.
    /// </summary>
    public static string? TryReadWeek(
        IReadOnlyCollection<OpeningHoursDayRequest>? days,
        out List<WeekdayHours> week)
    {
        week = [];
        var seen = new HashSet<DayOfWeek>();

        foreach (var entry in days ?? [])
        {
            if (ReadDay(entry.Day) is not DayOfWeek day)
            {
                return CourtErrorCodes.InvalidDay;
            }

            if (!seen.Add(day))
            {
                return CourtErrorCodes.DuplicateDay;
            }

            var invalidHours = ValidateHours(entry.OpensHour, entry.ClosesHour, closedAllowed: true);
            if (invalidHours is not null)
            {
                return invalidHours;
            }

            week.Add(new WeekdayHours(day, entry.OpensHour, entry.ClosesHour));
        }

        if (seen.Count != 7)
        {
            return CourtErrorCodes.MissingDay;
        }

        // A week with every day closed sells nothing; a venue closing for a while uses a closure.
        return week.All(day => day.OpensHour is null) ? CourtErrorCodes.NeverOpen : null;
    }

    /// <summary>
    /// Where the venue's day has to start once this week is published, or the reason it cannot
    /// be (thai-fit T4). The line only rises — <paramref name="dayStartsHour"/> is where it stands
    /// — and no day may open before it: Saturday opening at 01:00 after Friday sold 01:00 as
    /// Friday's would be one hour on two days.
    /// </summary>
    public static string? DayStartFor(
        IReadOnlyCollection<WeekdayHours> week,
        int dayStartsHour,
        out int startsHour)
    {
        startsHour = Math.Max(
            dayStartsHour, Localization.VenueClock.DayStartsHourFor(week.Select(day => day.ClosesHour)));
        var line = startsHour;
        return week.Any(day => day.OpensHour < line) ? CourtErrorCodes.OpensBeforeLastNightCloses : null;
    }

    /// <summary>
    /// The hours a price band may cover: the same grammar as a day, except that it may start past
    /// midnight, because the hours after midnight are priced on the day the venue opened.
    /// </summary>
    internal static string? ValidateBandHours(int fromHour, int toHour) =>
        fromHour < EarliestOpeningHour || toHour > LatestClosingHour || toHour <= fromHour
            ? CourtErrorCodes.InvalidHours
            : null;

}
