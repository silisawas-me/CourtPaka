namespace CourtBooking.Api.Venues;

/// <summary>
/// One playable court at a venue. Availability, bookings and the utilisation report are all counted
/// per court-hour, so a court is the unit everything else hangs off (PRD US-11, US-15).
/// </summary>
public sealed class Court
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    /// <summary>What the venue calls it, shown as a column in the availability grid.</summary>
    public required string Name { get; set; }

    /// <summary>Where it sits in that grid; venues rarely list courts alphabetically.</summary>
    public required int Position { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public Venue? Venue { get; init; }

    /// <summary>
    /// A court and the row saying it is in use, which are always written together: a court with no
    /// status row would read as out of use, and the utilisation report would undercount it.
    /// </summary>
    public static (Court Court, CourtStatusChange Status) Open(
        Guid venueId,
        string name,
        int position,
        Guid byUserId,
        DateOnly from,
        DateTimeOffset at)
    {
        var court = new Court
        {
            VenueId = venueId,
            Name = name,
            Position = position,
            CreatedAt = at,
        };

        return (court, court.ChangeStatus(active: true, from, byUserId, at));
    }

    public CourtStatusChange ChangeStatus(bool active, DateOnly from, Guid byUserId, DateTimeOffset at) =>
        new()
        {
            CourtId = Id,
            Active = active,
            EffectiveFrom = from,
            ChangedByUserId = byUserId,
            ChangedAt = at,
        };
}

/// <summary>
/// A court being taken out of use or put back, from a date. This is the only record of whether a
/// court is in use: asking about today answers the settings screen, asking about a day next week
/// answers the availability grid, and asking about last month answers the report (PRD US-11, US-15).
/// </summary>
public sealed class CourtStatusChange
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid CourtId { get; init; }

    public required bool Active { get; init; }

    /// <summary>The first Bangkok date this state applies to.</summary>
    public required DateOnly EffectiveFrom { get; init; }

    public required Guid ChangedByUserId { get; init; }

    public required DateTimeOffset ChangedAt { get; init; }

    public Court? Court { get; init; }
}

/// <summary>
/// One version of a venue's week: when it opens and closes on each weekday, from a given date.
/// Versions are only ever added, so what the venue was open for on a past date stays answerable
/// (PRD US-11, US-15).
/// </summary>
public sealed class OpeningHoursSchedule
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    /// <summary>The first Bangkok date this week applies to.</summary>
    public required DateOnly EffectiveFrom { get; init; }

    public required Guid CreatedByUserId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public List<OpeningHoursDay> Days { get; init; } = [];

    public Venue? Venue { get; init; }

    public static OpeningHoursSchedule Create(
        Guid venueId,
        DateOnly effectiveFrom,
        IEnumerable<WeekdayHours> week,
        Guid byUserId,
        DateTimeOffset at)
    {
        var schedule = new OpeningHoursSchedule
        {
            VenueId = venueId,
            EffectiveFrom = effectiveFrom,
            CreatedByUserId = byUserId,
            CreatedAt = at,
        };

        schedule.Days.AddRange(week.Select(day => new OpeningHoursDay
        {
            ScheduleId = schedule.Id,
            Day = day.Day,
            OpensHour = day.OpensHour,
            ClosesHour = day.ClosesHour,
        }));

        return schedule;
    }
}

/// <summary>
/// One weekday inside a schedule. Both hours null means the venue is closed that day; otherwise the
/// venue sells the whole hours from <see cref="OpensHour"/> up to <see cref="ClosesHour"/>.
/// </summary>
public sealed class OpeningHoursDay
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid ScheduleId { get; init; }

    public required DayOfWeek Day { get; init; }

    /// <summary>0–23, or null when closed.</summary>
    public int? OpensHour { get; init; }

    /// <summary>1–24, where 24 is midnight at the end of the day, or null when closed.</summary>
    public int? ClosesHour { get; init; }

    public OpeningHoursSchedule? Schedule { get; init; }
}

/// <summary>One weekday as the venue asked for it, after validation and before it becomes a row.</summary>
public sealed record WeekdayHours(DayOfWeek Day, int? OpensHour, int? ClosesHour);

/// <summary>
/// What the venue's settings said on a given date. Both settings are append-only timelines, and
/// both are read the same way — the newest entry dated on or before the date wins, and if two share
/// a date the one written last does — so the rule lives here once rather than in each caller.
///
/// Both timelines hold a handful of rows per venue per year, so they are resolved in memory: one
/// rule in one place is worth more here than pushing two variants of it into SQL.
/// </summary>
public static class VenueTimeline
{
    public static bool CourtIsActiveOn(IEnumerable<CourtStatusChange> changes, Guid courtId, DateOnly on) =>
        changes
            .Where(change => change.CourtId == courtId && change.EffectiveFrom <= on)
            .OrderByDescending(change => change.EffectiveFrom)
            .ThenByDescending(change => change.ChangedAt)
            .Select(change => change.Active)
            .FirstOrDefault();

    public static OpeningHoursSchedule? OpeningHoursOn(
        IEnumerable<OpeningHoursSchedule> schedules,
        DateOnly on) =>
        schedules
            .Where(schedule => schedule.EffectiveFrom <= on)
            .OrderByDescending(schedule => schedule.EffectiveFrom)
            .ThenByDescending(schedule => schedule.CreatedAt)
            .FirstOrDefault();

    /// <summary>
    /// The versions worth showing an owner: the one running today, plus the newest version for each
    /// date ahead of it. Superseded versions stay in the database; nothing needs them on screen.
    /// </summary>
    public static IEnumerable<OpeningHoursSchedule> CurrentAndUpcoming(
        IEnumerable<OpeningHoursSchedule> schedules,
        DateOnly today)
    {
        var all = schedules.ToList();
        var inForce = OpeningHoursOn(all, today);

        return all
            .Where(schedule => schedule.EffectiveFrom > today)
            .GroupBy(schedule => schedule.EffectiveFrom)
            .Select(group => group.OrderByDescending(schedule => schedule.CreatedAt).First())
            .Prepend(inForce!)
            .Where(schedule => schedule is not null)
            .OrderBy(schedule => schedule.EffectiveFrom);
    }
}
