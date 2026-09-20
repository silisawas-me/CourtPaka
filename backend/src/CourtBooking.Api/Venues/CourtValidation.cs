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

    /// <summary>Midnight at the end of the day, so a venue that closes at 00:00 is expressible.</summary>
    public const int LatestClosingHour = 24;

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
            || endHour is < EarliestClosingHour or > LatestClosingHour)
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

}
