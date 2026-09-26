using System.Globalization;

namespace CourtBooking.Api.Localization;

/// <summary>
/// Business rules are evaluated in Thai time (PRD BR-10) and documents are rendered in Thai.
/// Linux images without ICU or tzdata silently break both, so the API refuses to start instead.
/// </summary>
public static class PlatformRequirements
{
    public const string BangkokTimeZoneId = "Asia/Bangkok";
    public const string ThaiCultureName = "th-TH";

    private static TimeZoneInfo? _bangkokTimeZone;

    public static TimeZoneInfo BangkokTimeZone => _bangkokTimeZone ??= FindBangkokTimeZone();

    /// <summary>
    /// Fails fast with a message that names the cause, instead of a stack trace from a static initializer.
    /// </summary>
    public static void EnsureAvailable()
    {
        _bangkokTimeZone = FindBangkokTimeZone();

        try
        {
            // Throws in globalization-invariant mode, which is what a runtime image without ICU gives us.
            _ = CultureInfo.GetCultureInfo(ThaiCultureName, predefinedOnly: true);
        }
        catch (CultureNotFoundException exception)
        {
            throw new InvalidOperationException(
                $"Culture '{ThaiCultureName}' is unavailable. The runtime image is missing ICU.", exception);
        }
    }

    /// <summary>
    /// Today in Thai time. Opening hours, closures and every report are counted by the Bangkok date,
    /// never the server's (PRD BR-10), so nothing may call DateTime.Today.
    /// </summary>
    public static DateOnly BangkokToday(TimeProvider timeProvider) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), BangkokTimeZone).DateTime);

    /// <summary>
    /// The range a report was asked for, or this calendar month when it was not. Three screens
    /// default the same way — the venue's dashboard, the platform's, and what a venue paid out —
    /// and a fourth spelling of it is a fourth chance to pick a different month.
    /// </summary>
    public static (DateOnly First, DateOnly Last) MonthOr(
        DateOnly? from,
        DateOnly? to,
        TimeProvider timeProvider)
    {
        var today = BangkokToday(timeProvider);
        var first = from ?? new DateOnly(today.Year, today.Month, 1);
        return (first, to ?? first.AddMonths(1).AddDays(-1));
    }

    /// <summary>
    /// The instant a Bangkok date and whole hour begins, as UTC. Hours are chosen as "6pm on the
    /// 5th" at the venue but stored in UTC (PRD BR-10), and Postgres timestamptz only accepts an
    /// offset of zero. Thailand has no daylight saving, so the conversion is total: every local
    /// hour exists exactly once.
    /// </summary>
    public static DateTimeOffset BangkokHour(DateOnly date, int hour)
    {
        var local = date.ToDateTime(TimeOnly.MinValue).AddHours(hour);
        return new DateTimeOffset(local, BangkokTimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>The Bangkok date and hour an instant falls in, which is how a slot is shown.</summary>
    public static (DateOnly Date, int Hour) BangkokDateAndHour(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, BangkokTimeZone).DateTime;
        return (DateOnly.FromDateTime(local), local.Hour);
    }

    private static TimeZoneInfo FindBangkokTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(BangkokTimeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new InvalidOperationException(
                $"Time zone '{BangkokTimeZoneId}' is unavailable. The runtime image is missing tzdata.", exception);
        }
    }
}
