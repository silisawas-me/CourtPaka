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
