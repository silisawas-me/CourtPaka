namespace CourtBooking.Api.Venues;

/// <summary>
/// Everything a court or a week of opening hours has to satisfy before it reaches the database.
/// Kept apart from the endpoints so the rules can be read, and tested, on their own.
/// </summary>
public static class CourtValidation
{
    public const int NameMaxLength = 50;
    public const int PositionMax = 500;

    /// <summary>Midnight at the end of the day, so a venue that closes at 00:00 is expressible.</summary>
    public const int LatestClosingHour = 24;

    public static string? ValidateName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > NameMaxLength
            ? CourtErrorCodes.InvalidCourtName
            : null;
    }

    public static string? ValidatePosition(int position) =>
        position is < 0 or > PositionMax ? CourtErrorCodes.InvalidPosition : null;

    /// <summary>
    /// Turns the seven submitted days into rows, or names the first thing wrong with them. A week
    /// has to be complete: leaving a day out would otherwise read as "closed" by accident.
    /// </summary>
    public static string? TryReadWeek(
        IReadOnlyCollection<OpeningHoursDayRequest>? days,
        out List<(DayOfWeek Day, int? OpensHour, int? ClosesHour)> week)
    {
        week = [];
        var seen = new HashSet<DayOfWeek>();

        foreach (var entry in days ?? [])
        {
            if (!Enum.TryParse<DayOfWeek>(entry.Day, ignoreCase: true, out var day)
                || !Enum.IsDefined(day))
            {
                return CourtErrorCodes.InvalidDay;
            }

            if (!seen.Add(day))
            {
                return CourtErrorCodes.DuplicateDay;
            }

            var invalidHours = ValidateHours(entry.OpensHour, entry.ClosesHour);
            if (invalidHours is not null)
            {
                return invalidHours;
            }

            week.Add((day, entry.OpensHour, entry.ClosesHour));
        }

        if (seen.Count != 7)
        {
            return CourtErrorCodes.MissingDay;
        }

        // A week with every day closed sells nothing; a venue closing for a while uses a closure.
        return week.All(day => day.OpensHour is null) ? CourtErrorCodes.NeverOpen : null;
    }

    private static string? ValidateHours(int? opens, int? closes)
    {
        if (opens is null && closes is null)
        {
            return null;
        }

        return opens is null or < 0 or > 23
               || closes is null or < 1 or > LatestClosingHour
               || closes <= opens
            ? CourtErrorCodes.InvalidHours
            : null;
    }
}
