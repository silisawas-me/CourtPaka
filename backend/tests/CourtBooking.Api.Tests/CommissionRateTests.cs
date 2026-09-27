using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>
/// What the platform charges a venue, from when (PRD US-21, BR-08). The first acceptance
/// criterion of the story: a rate per venue, with a date it takes effect, and the history kept.
/// </summary>
public sealed class CommissionRateTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    private static readonly DateOnly Today = VenueScenario.Today;

    [Fact]
    public async Task A_venue_the_platform_has_said_nothing_about_has_no_rate()
    {
        var (admin, venueId) = await AdminAndVenueAsync();

        var rates = await ReadAsync(admin, venueId);

        // Not nought: nought is a rate somebody chose, and this is a question nobody has answered.
        Assert.Null(rates.TodayPercent);
        Assert.Empty(rates.Rates);
    }

    [Fact]
    public async Task A_rate_is_agreed_from_a_date_and_kept()
    {
        var (admin, venueId) = await AdminAndVenueAsync();

        var set = await SetAsync(admin, venueId, 12.5m, Today, "ตามที่ตกลงกัน");

        Assert.Equal(12.5m, set.TodayPercent);
        var only = Assert.Single(set.Rates);
        Assert.Equal(12.5m, only.Percent);
        Assert.Equal(Today, only.EffectiveFrom);
        Assert.Equal("ตามที่ตกลงกัน", only.Note);
        Assert.NotNull(only.SetByEmail);
    }

    /// <summary>
    /// A rate is not a setting that gets edited. The old one is what the days before the new one
    /// are charged at, and an invoice months later has to arrive at the same number.
    /// </summary>
    [Fact]
    public async Task Agreeing_a_new_rate_keeps_the_old_one()
    {
        var (admin, venueId) = await AdminAndVenueAsync();

        await SetAsync(admin, venueId, 10m, Today);
        var after = await SetAsync(admin, venueId, 8m, Today.AddDays(30));

        // Still ten today: the new one has not started yet.
        Assert.Equal(10m, after.TodayPercent);
        Assert.Equal(2, after.Rates.Length);
        Assert.Equal([Today.AddDays(30), Today], after.Rates.Select(rate => rate.EffectiveFrom));
    }

    /// <summary>
    /// The platform tells a venue about a change before it starts, so a date ahead is ordinary.
    /// A date behind is not: those days have been played and already charged at what was in
    /// force then, and moving that would change a month the venue has been told about (BR-08).
    /// </summary>
    [Fact]
    public async Task A_rate_may_start_later_but_never_earlier()
    {
        var (admin, venueId) = await AdminAndVenueAsync();

        var ahead = await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/commission",
            new SetCommissionRateRequest(9m, Today.AddDays(1), null));
        Assert.Equal(HttpStatusCode.OK, ahead.StatusCode);

        var behind = await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/commission",
            new SetCommissionRateRequest(9m, Today.AddDays(-1), null));

        Assert.Equal(HttpStatusCode.BadRequest, behind.StatusCode);
        Assert.Equal(VenueErrorCodes.RateStartsInThePast, await behind.ErrorCodeAsync());
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    [InlineData(12.345)]
    public async Task A_number_that_could_not_be_a_rate_is_refused(decimal percent)
    {
        var (admin, venueId) = await AdminAndVenueAsync();

        var refused = await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venueId}/commission",
            new SetCommissionRateRequest(percent, Today, null));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(VenueErrorCodes.InvalidRate, await refused.ErrorCodeAsync());
    }

    /// <summary>
    /// What a booking is charged is the rate in force on the day it was played — not the day it
    /// was booked, and not the rate in force when the invoice is built (PRD BR-08).
    /// </summary>
    [Fact]
    public void A_booking_is_charged_the_rate_in_force_on_the_day_it_was_played()
    {
        var rates = new[]
        {
            Rate(10m, new DateOnly(2027, 1, 1)),
            Rate(8m, new DateOnly(2027, 6, 1)),
        };

        Assert.Null(Commission.InForceOn(rates, new DateOnly(2026, 12, 31)));
        Assert.Equal(10m, Commission.InForceOn(rates, new DateOnly(2027, 1, 1))!.Percent);
        Assert.Equal(10m, Commission.InForceOn(rates, new DateOnly(2027, 5, 31))!.Percent);
        Assert.Equal(8m, Commission.InForceOn(rates, new DateOnly(2027, 6, 1))!.Percent);
        Assert.Equal(8m, Commission.InForceOn(rates, new DateOnly(2030, 1, 1))!.Percent);
    }

    /// <summary>Two rates from the same day is the platform correcting itself; the later wins.</summary>
    [Fact]
    public void A_correction_entered_later_wins_over_the_one_it_corrects()
    {
        var day = new DateOnly(2027, 3, 1);
        var rates = new[]
        {
            Rate(10m, day, new DateTimeOffset(2027, 2, 1, 9, 0, 0, TimeSpan.Zero)),
            Rate(7m, day, new DateTimeOffset(2027, 2, 2, 9, 0, 0, TimeSpan.Zero)),
        };

        Assert.Equal(7m, Commission.InForceOn(rates, day)!.Percent);
    }

    [Theory]
    [InlineData(1000, 10, 100)]
    [InlineData(333.33, 12.5, 41.67)]
    [InlineData(0, 10, 0)]
    public void What_one_booking_adds_is_the_share_of_what_the_venue_kept(
        decimal kept, decimal percent, decimal expected) =>
        Assert.Equal(expected, Commission.On(kept, percent));

    /// <summary>The rate is the platform's business, and nobody else's to read or to set.</summary>
    [Fact]
    public async Task A_venue_cannot_see_or_set_what_it_is_charged()
    {
        var (_, venueId) = await AdminAndVenueAsync();
        var owner = await scenario.SignedInClientAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await owner.GetAsync($"/api/admin/venues/{venueId}/commission")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await owner.PostAsJsonAsync(
                $"/api/admin/venues/{venueId}/commission",
                new SetCommissionRateRequest(0m, Today, null))).StatusCode);
    }

    private static CommissionRate Rate(decimal percent, DateOnly from, DateTimeOffset? setAt = null) =>
        new()
        {
            VenueId = Guid.Empty,
            Percent = percent,
            EffectiveFrom = from,
            SetByUserId = Guid.Empty,
            SetAt = setAt ?? DateTimeOffset.UnixEpoch,
        };

    private async Task<(HttpClient Admin, Guid VenueId)> AdminAndVenueAsync()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (_, venue, _) = await scenario.BookableVenueAsync();
        return (admin, venue.Id);
    }

    private static async Task<CommissionRatesResponse> SetAsync(
        HttpClient admin,
        Guid venueId,
        decimal percent,
        DateOnly from,
        string? note = null) =>
        await VenueScenario.ReadAsync<CommissionRatesResponse>(
            await admin.PostAsJsonAsync(
                $"/api/admin/venues/{venueId}/commission",
                new SetCommissionRateRequest(percent, from, note)));

    private static async Task<CommissionRatesResponse> ReadAsync(HttpClient admin, Guid venueId) =>
        await VenueScenario.ReadAsync<CommissionRatesResponse>(
            await admin.GetAsync($"/api/admin/venues/{venueId}/commission"));
}
