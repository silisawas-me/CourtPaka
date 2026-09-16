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

    public static TimeZoneInfo BangkokTimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById(BangkokTimeZoneId);

    public static void EnsureAvailable()
    {
        _ = BangkokTimeZone;

        var thai = CultureInfo.GetCultureInfo(ThaiCultureName, predefinedOnly: true);
        if (thai.DateTimeFormat.MonthNames[0] == CultureInfo.InvariantCulture.DateTimeFormat.MonthNames[0])
        {
            throw new InvalidOperationException(
                $"Culture '{ThaiCultureName}' has no Thai data. ICU is missing from the runtime image.");
        }
    }
}
