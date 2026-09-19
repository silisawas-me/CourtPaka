namespace CourtBooking.Api.Venues;

/// <summary>
/// What one court-hour looks like to someone deciding whether to book it (PRD US-02).
/// </summary>
public enum HourStatus
{
    /// <summary>The venue is open, the court is in use, and the hour can be booked.</summary>
    Free = 1,

    /// <summary>Outside the venue's opening hours for that day, or the court is out of use.</summary>
    Closed = 2,
}

/// <summary>
/// The court-hours of one day at one venue, built from the settings in force for that date: the
/// courts in use, the week the venue is open for, and the prices a booking made now would pay.
///
/// It says nothing about who booked what. A booker sees whether an hour is theirs to take and what
/// it costs, never another booker (PRD US-02).
/// </summary>
public static class Availability
{
    /// <summary>How far ahead a booker may look, and therefore book (PRD S-04).</summary>
    public const int BookableDaysAhead = 30;

    public static string? ValidateDate(DateOnly date, DateOnly today) =>
        date < today
            ? AvailabilityErrorCodes.DateInThePast
            : date > today.AddDays(BookableDaysAhead)
                ? AvailabilityErrorCodes.DateTooFarAhead
                : null;

    /// <summary>
    /// One row per court, one cell per hour of the venue's longest day. Hours outside the day are
    /// closed rather than missing, so every row is the same length and the grid lines up.
    /// </summary>
    public static AvailabilityResponse Build(
        DateOnly date,
        IReadOnlyCollection<Court> courts,
        IReadOnlyCollection<CourtStatusChange> statusChanges,
        OpeningHoursSchedule? week,
        IReadOnlyCollection<PriceBand> bands)
    {
        var day = week?.Days.SingleOrDefault(entry => entry.Day == date.DayOfWeek);
        var opens = day?.OpensHour;
        var closes = day?.ClosesHour;

        var hours = opens is int from && closes is int until
            ? Enumerable.Range(from, until - from).ToArray()
            : [];

        var rows = courts
            .OrderBy(court => court.Position)
            .ThenBy(court => court.Name)
            .Select(court =>
            {
                var inUse = VenueTimeline.CourtIsActiveOn(statusChanges, court.Id, date);
                return new CourtAvailabilityResponse(
                    court.Id,
                    court.Name,
                    hours
                        .Select(hour => new HourResponse(
                            hour,
                            inUse ? HourStatus.Free.ToString() : HourStatus.Closed.ToString(),
                            inUse ? PriceFor(bands, date.DayOfWeek, hour) : null))
                        .ToArray());
            })
            .ToArray();

        return new AvailabilityResponse(date, opens, closes, rows);
    }

    /// <summary>
    /// What this hour costs. The settings refuse to leave an open hour unpriced, so a null here
    /// means the data was written before that rule held — and an hour with no price is not for sale.
    /// </summary>
    private static decimal? PriceFor(IReadOnlyCollection<PriceBand> bands, DayOfWeek day, int hour) =>
        bands
            .Where(band => band.Day == day && band.FromHour <= hour && hour < band.ToHour)
            .Select(band => (decimal?)band.BahtPerHour)
            .FirstOrDefault();
}
