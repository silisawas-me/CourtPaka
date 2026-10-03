using System.Globalization;
using CourtBooking.Api.Localization;

namespace CourtBooking.Api.Tests;

public sealed class PlatformRequirementsTests
{
    [Fact]
    public void Bangkok_time_zone_is_utc_plus_seven()
    {
        Assert.Equal(TimeSpan.FromHours(7), PlatformRequirements.BangkokTimeZone.BaseUtcOffset);
    }

    [Fact]
    public void Thai_culture_data_is_available()
    {
        PlatformRequirements.EnsureAvailable();

        var thai = CultureInfo.GetCultureInfo(PlatformRequirements.ThaiCultureName);
        Assert.Equal("มกราคม", thai.DateTimeFormat.MonthNames[0]);
    }
}
