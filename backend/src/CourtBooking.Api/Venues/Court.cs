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

    /// <summary>
    /// Whether the court is in use today. The history in <see cref="CourtStatusChange"/> answers the
    /// same question about a past date, which the utilisation report needs (PRD US-15).
    /// </summary>
    public required bool IsActive { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public Venue? Venue { get; init; }
}

/// <summary>
/// A court being taken out of use or put back. Kept as its own row rather than a flag on the court,
/// because a report about last month has to know what the venue looked like last month (PRD US-11).
/// </summary>
public sealed class CourtStatusChange
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid CourtId { get; init; }

    public required bool Active { get; init; }

    /// <summary>The first Bangkok date this state applied to.</summary>
    public required DateOnly EffectiveFrom { get; init; }

    public required Guid ChangedByUserId { get; init; }

    public required DateTimeOffset ChangedAt { get; init; }

    public Court? Court { get; init; }
}

/// <summary>
/// One version of a venue's week: when it opens and closes on each weekday, from a given date. A new
/// version never overwrites an old one, so what the venue sold in the past stays answerable
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

    public bool IsClosed => OpensHour is null || ClosesHour is null;

    public OpeningHoursSchedule? Schedule { get; init; }
}
