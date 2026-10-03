using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>
/// A venue that stays open past midnight (docs/plan/thai-fit.md T4): Friday 16:00 until 02:00
/// Saturday. The hours after midnight are Friday's hours 24 and 25 — on Friday's grid, in Friday's
/// list, at Friday's price — and no day may open before the night before has finished.
/// </summary>
public sealed class LateNightTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    /// <summary>The next Friday that is not today, so its last hours are still to come.</summary>
    private static DateOnly NextFriday()
    {
        var day = VenueScenario.Today.AddDays(1);
        while (day.DayOfWeek != DayOfWeek.Friday)
        {
            day = day.AddDays(1);
        }

        return day;
    }

    /// <summary>Six to ten every day, except Friday, which runs from four in the afternoon to two.</summary>
    private static OpeningHoursDayRequest[] LateFriday(int saturdayOpens = 6) =>
        Enum.GetValues<DayOfWeek>()
            .Select(day => day switch
            {
                DayOfWeek.Friday => new OpeningHoursDayRequest(day.ToString(), 16, 26),
                DayOfWeek.Saturday => new OpeningHoursDayRequest(day.ToString(), saturdayOpens, 22),
                _ => new OpeningHoursDayRequest(day.ToString(), 6, 22),
            })
            .ToArray();

    private async Task<(HttpClient Owner, Guid VenueId, Guid CourtId)> LateVenueAsync()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        await VenueScenario.SetPricesAsync(
            owner,
            venue.Id,
            [
                .. VenueScenario.AllWeek(0, 24, 200m),
                new PriceBandRequest(nameof(DayOfWeek.Friday), 24, 26, 250m),
            ]);
        var published = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/opening-hours",
            new SetOpeningHoursRequest(VenueScenario.Today, LateFriday()));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        return (owner, venue.Id, courts[0]);
    }

    [Fact]
    public void The_hours_after_midnight_belong_to_the_day_the_venue_opened()
    {
        var saturdayAtOne = PlatformRequirements.BangkokHour(new DateOnly(2026, 10, 10), 1);

        Assert.Equal((new DateOnly(2026, 10, 9), 25), VenueClock.DayAndHour(saturdayAtOne, 2));
        Assert.Equal((new DateOnly(2026, 10, 10), 1), VenueClock.DayAndHour(saturdayAtOne, 0));
        Assert.Equal(
            PlatformRequirements.BangkokHour(new DateOnly(2026, 10, 9), 25),
            saturdayAtOne);

        var (from, until) = VenueClock.Window(new DateOnly(2026, 10, 9), 2);
        Assert.Equal(PlatformRequirements.BangkokHour(new DateOnly(2026, 10, 9), 2), from);
        Assert.Equal(saturdayAtOne.AddHours(1), until);
        Assert.Equal(2, VenueClock.DayStartsHourFor([22, 26, null, 24]));
        Assert.Equal(0, VenueClock.DayStartsHourFor([22, 24]));
    }

    [Fact]
    public async Task A_week_that_closes_at_two_moves_where_the_venue_day_starts()
    {
        var (owner, venueId, _) = await LateVenueAsync();

        var venue = await VenueScenario.ReadAsync<VenueResponse>(await owner.GetAsync($"/api/venues/{venueId}"));
        Assert.Equal(2, venue.DayStartsHour);

        // Friday's grid runs to 02:00, and the hours after midnight carry Friday's late price.
        var friday = await scenario.ReadAvailabilityAsync(owner, venueId, NextFriday());
        Assert.Equal(26, friday.ClosesHour);
        var late = friday.Courts.Single().Hours.Where(hour => hour.Hour >= 24).ToArray();
        Assert.Equal([24, 25], late.Select(hour => hour.Hour));
        Assert.All(late, hour => Assert.Equal(250m, hour.BahtPerHour));
    }

    [Fact]
    public async Task One_in_the_morning_is_sold_and_listed_as_friday()
    {
        var (owner, venueId, courtId) = await LateVenueAsync();
        var friday = NextFriday();

        var sold = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/bookings",
                new CounterBookingRequest(
                    [new BookingSlotRequest(courtId, friday, 25)], "คุณดึก", null, nameof(CounterPayment.Cash))),
            HttpStatusCode.Created);

        var slot = Assert.Single(sold.Slots);
        Assert.Equal((friday, 25), (slot.Date, slot.Hour));
        Assert.Equal(250m, sold.TotalBaht);

        // It is played at 01:00 on the Saturday, and nobody else can have that hour.
        using (var scope = api.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var startsAt = await database.BookingSlots
                .Where(row => row.BookingId == sold.BookingId)
                .Select(row => row.StartsAt)
                .SingleAsync();
            Assert.Equal(PlatformRequirements.BangkokHour(friday.AddDays(1), 1), startsAt);
        }

        var again = await scenario.ReadAvailabilityAsync(owner, venueId, friday);
        Assert.Equal(
            nameof(HourStatus.Booked),
            again.Courts.Single().Hours.Single(hour => hour.Hour == 25).Status);

        // Friday's list has it; Saturday's does not.
        var fridayList = await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await owner.GetAsync($"/api/venues/{venueId}/bookings?date={friday:yyyy-MM-dd}"));
        var saturdayList = await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await owner.GetAsync($"/api/venues/{venueId}/bookings?date={friday.AddDays(1):yyyy-MM-dd}"));
        Assert.Contains(fridayList, one => one.BookingId == sold.BookingId);
        Assert.DoesNotContain(saturdayList, one => one.BookingId == sold.BookingId);
    }

    [Fact]
    public async Task No_day_opens_before_the_night_before_has_closed_and_the_line_never_falls_back()
    {
        var (owner, venueId, _) = await LateVenueAsync();

        var early = await owner.PutAsJsonAsync(
            $"/api/venues/{venueId}/opening-hours",
            new SetOpeningHoursRequest(VenueScenario.Today, LateFriday(saturdayOpens: 1)));
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
        Assert.Equal(CourtErrorCodes.OpensBeforeLastNightCloses, await early.ErrorCodeAsync());

        // Back to closing by midnight: the day still starts at two, so Friday's 01:00 already
        // sold stays Friday's.
        await VenueScenario.SetHoursAsync(owner, venueId, VenueScenario.Today, 6, 24);
        var venue = await VenueScenario.ReadAsync<VenueResponse>(await owner.GetAsync($"/api/venues/{venueId}"));
        Assert.Equal(2, venue.DayStartsHour);
    }
}
