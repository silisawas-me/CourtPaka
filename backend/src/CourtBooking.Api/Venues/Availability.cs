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

    /// <summary>Someone else holds it. Who, and for how much, is never said (PRD US-02).</summary>
    Booked = 3,
}

/// <summary>
/// The rules about which day a booker may look at, and how a <see cref="VenueDay"/> is drawn for
/// them. What a day actually holds is <see cref="VenueDay"/>; this is only the wire shape.
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
    public static AvailabilityResponse Draw(PublicVenueResponse venue, VenueDay day, DateOnly today)
    {
        var hours = day.Hours.ToArray();

        var rows = day.Courts
            .Select(court => new CourtAvailabilityResponse(
                court.Id,
                court.Name,
                hours
                    .Select(hour => new HourResponse(
                        hour,
                        day.Status(court.Id, hour).ToString(),
                        // A booked hour still shows its price: it is what the hour costs, not what
                        // anyone paid, and the grid reads as a price list either way.
                        day.Price(court.Id, hour)))
                    .ToArray()))
            .ToArray();

        return new AvailabilityResponse(
            venue,
            day.Date,
            today.AddDays(BookableDaysAhead),
            day.OpensHour,
            day.ClosesHour,
            rows,
            day.DayStartsHour);
    }
}
