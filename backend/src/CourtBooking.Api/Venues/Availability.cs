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
    /// One row per court in the order the venue lists them, one cell per hour the venue is open
    /// that day. A day the venue is closed has no cells at all, which is what lets a page say so
    /// rather than draw an empty grid.
    /// </summary>
    public static AvailabilityResponse Build(
        PublicVenueResponse venue,
        DateOnly date,
        DateOnly today,
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

        // What an hour costs is the venue's, not the court's, so it is read once for the row above.
        var priceByHour = hours.Select(hour => PriceFor(bands, date.DayOfWeek, hour)).ToArray();
        var free = HourStatus.Free.ToString();
        var closed = HourStatus.Closed.ToString();
        var statusByCourt = statusChanges.ToLookup(change => change.CourtId);

        var rows = courts
            .OrderBy(court => court.Position)
            .ThenBy(court => court.Name)
            .Select(court =>
            {
                var inUse = VenueTimeline.CourtIsActiveOn(statusByCourt[court.Id], court.Id, date);
                return new CourtAvailabilityResponse(
                    court.Id,
                    court.Name,
                    hours
                        .Select((hour, index) =>
                        {
                            // An hour with nothing to charge for it is not for sale, whatever the
                            // court is doing: a venue may publish its hours before its prices.
                            var baht = inUse ? priceByHour[index] : null;
                            return new HourResponse(hour, baht is null ? closed : free, baht);
                        })
                        .ToArray());
            })
            .ToArray();

        return new AvailabilityResponse(
            venue, date, today.AddDays(BookableDaysAhead), opens, closes, rows);
    }

    /// <summary>
    /// What this hour costs, or null when nothing prices it — which a venue that published its
    /// hours before its prices will have for every hour, and the caller reads as closed.
    /// </summary>
    private static decimal? PriceFor(IReadOnlyCollection<PriceBand> bands, DayOfWeek day, int hour) =>
        bands
            .Where(band => band.Day == day && band.FromHour <= hour && hour < band.ToHour)
            .Select(band => (decimal?)band.BahtPerHour)
            .FirstOrDefault();
}
