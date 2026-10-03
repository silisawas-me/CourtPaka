namespace CourtBooking.Api.Localization;

/// <summary>
/// A venue's day, which is not the calendar's (docs/plan/thai-fit.md T4). A court open Friday
/// 16:00 until 02:00 Saturday sells its last two hours as Friday's hours 24 and 25: the booking,
/// the day's list, the drawer and the report all put them on Friday, the day the venue opened.
///
/// So a venue's day runs from its <c>DayStartsHour</c> to the same hour the next morning. A venue
/// that never closes past midnight starts its day at 0, and everything here reads exactly as the
/// calendar does. Paperwork — refunds, commission rates, package expiry — stays on the calendar;
/// this is for what happens on the floor.
/// </summary>
public static class VenueClock
{
    /// <summary>The latest a venue's day may start: a venue can stay open until 06:00.</summary>
    public const int LatestDayStart = 6;

    /// <summary>When a venue's day begins and ends, as UTC instants.</summary>
    public static (DateTimeOffset From, DateTimeOffset Until) Window(DateOnly day, int dayStartsHour) =>
        (PlatformRequirements.BangkokHour(day, dayStartsHour),
         PlatformRequirements.BangkokHour(day.AddDays(1), dayStartsHour));

    /// <summary>
    /// The venue's day an instant falls on, and its hour in that day — 24 and up for the hours
    /// after midnight. The inverse of <see cref="PlatformRequirements.BangkokHour"/>.
    /// </summary>
    public static (DateOnly Date, int Hour) DayAndHour(DateTimeOffset at, int dayStartsHour)
    {
        var (date, hour) = PlatformRequirements.BangkokDateAndHour(at);
        return hour < dayStartsHour ? (date.AddDays(-1), hour + 24) : (date, hour);
    }

    /// <summary>The venue's day it is now. At 01:00 Saturday a venue open until 02:00 is still on Friday.</summary>
    public static DateOnly Today(TimeProvider timeProvider, int dayStartsHour) =>
        DayAndHour(timeProvider.GetUtcNow(), dayStartsHour).Date;

    /// <summary>
    /// How far past midnight a week runs, which is where the venue's day has to start for none of
    /// its hours to belong to two days. Zero for a week that closes by midnight.
    /// </summary>
    public static int DayStartsHourFor(IEnumerable<int?> closingHours) =>
        Math.Max(0, closingHours.Max(closes => closes ?? 0) - 24);
}
