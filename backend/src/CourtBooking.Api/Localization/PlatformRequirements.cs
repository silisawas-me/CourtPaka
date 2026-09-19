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
}
