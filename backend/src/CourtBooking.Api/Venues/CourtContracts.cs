namespace CourtBooking.Api.Venues;

public sealed record CreateCourtRequest(string Name);

public sealed record UpdateCourtRequest(string Name, int Position);

/// <summary>
/// Taking a court out of use, or putting it back, from a date. Without one it applies from today
/// in Thai time. Closing a court for a stretch of dates is a closure instead (PRD US-11).
/// </summary>
public sealed record ChangeCourtStatusRequest(bool Active, DateOnly? EffectiveFrom = null);

public sealed record CourtResponse(Guid Id, string Name, int Position, bool IsActive);

public sealed record CourtStatusChangeResponse(bool Active, DateOnly EffectiveFrom, DateTimeOffset ChangedAt);

/// <summary>One weekday. Both hours null means the venue does not open that day.</summary>
public sealed record OpeningHoursDayRequest(string Day, int? OpensHour, int? ClosesHour);

public sealed record SetOpeningHoursRequest(DateOnly EffectiveFrom, OpeningHoursDayRequest[] Days);

public sealed record OpeningHoursDayResponse(string Day, int? OpensHour, int? ClosesHour);

/// <summary>
/// A schedule version. The list a venue gets back holds the one in force plus anything dated ahead
/// of it, so an owner can see a change they have already made but which has not started yet.
/// </summary>
public sealed record OpeningHoursResponse(
    Guid Id,
    DateOnly EffectiveFrom,
    bool InForce,
    OpeningHoursDayResponse[] Days);

public static class CourtErrorCodes
{
    public const string InvalidCourtName = "court.invalid_name";
    public const string NameAlreadyUsed = "court.name_already_used";
    public const string InvalidPosition = "court.invalid_position";
    public const string EffectiveDateInThePast = "court.effective_date_in_the_past";
    public const string InvalidDay = "opening_hours.invalid_day";
    public const string InvalidHours = "opening_hours.invalid_hours";
    public const string DuplicateDay = "opening_hours.duplicate_day";
    public const string MissingDay = "opening_hours.missing_day";
    public const string NeverOpen = "opening_hours.never_open";
}
