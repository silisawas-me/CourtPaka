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

/// <summary>
/// What the court's timeline says after a change: how it stands today, which is what the settings
/// screen shows, and anything already dated ahead — so a caller can tell a change that was recorded
/// from one the timeline already said, and cannot be surprised by a closure someone scheduled.
/// </summary>
public sealed record CourtStatusResponse(
    Guid CourtId,
    bool ActiveToday,
    CourtStatusChangeResponse[] Scheduled);

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

    public const string InvalidClosureReason = "closure.invalid_reason";

    /// <summary>The range is backwards, is not whole hours, or has already been and gone.</summary>
    public const string InvalidClosureRange = "closure.invalid_range";

    /// <summary>
    /// Bookings stand inside the stretch the venue wants to shut. The venue cancels them —
    /// with a reason, which is what decides what goes back (PRD 6.1) — and asks again.
    /// </summary>
    public const string BookingsInTheWay = "closure.bookings_in_the_way";

    public const string CourtNotFound = "court.not_found";
    public const string ClosureNotFound = "closure.not_found";
    public const string ClosureAlreadyLifted = "closure.already_lifted";
}

/// <summary>
/// Shutting a court for a stretch of time (PRD US-11). Named the way the rest of the system names
/// times — a Thai date and a whole hour of it — rather than as an instant, because that is what
/// the venue is choosing on the screen and what an hour of the grid is.
///
/// <paramref name="EndsOn"/> with <paramref name="EndHour"/> is the first hour the court is back,
/// so shutting a single evening hour is the same shape as shutting a fortnight.
/// </summary>
public sealed record CloseCourtRequest(
    DateOnly StartsOn,
    int StartHour,
    DateOnly EndsOn,
    int EndHour,
    string Reason);

/// <summary>One booking a closure would have to displace, as the venue sees it before deciding.</summary>
/// <remarks>
/// No booker on it. Who booked is behind <c>ManageBookings</c> (PDPA, PRD 8, US-14) and this list
/// is behind <c>CloseCourt</c> — the venue is told what it has to deal with and where, and reads
/// the rest on the screen that is allowed to show it.
/// </remarks>
public sealed record ClashingBookingResponse(
    Guid BookingId,
    Guid CourtId,
    string CourtName,
    DateOnly Date,
    int FromHour,
    int ToHour,
    string Status);

public sealed record CourtClosureResponse(
    Guid Id,
    Guid CourtId,
    DateOnly StartsOn,
    int StartHour,
    DateOnly EndsOn,
    int EndHour,
    string Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LiftedAt);
