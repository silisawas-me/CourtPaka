using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>What a venue charges, and what it gives back on a cancellation (PRD US-11, BR-05).</summary>
[Collection(ApiCollection.Name)]
public sealed class PricingTests(ApiTestFixture api)
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task A_venue_with_no_prices_yet_says_so_rather_than_inventing_any()
    {
        var (owner, venue) = await OpenVenueAsync();

        var response = await owner.GetAsync($"/api/venues/{venue.Id}/prices");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task The_published_prices_come_back_in_reading_order()
    {
        var (owner, venue) = await OpenVenueAsync();

        await SetPricesAsync(owner, venue.Id, AllWeek(6, 18, 200m).Concat(AllWeek(18, 22, 300m)));

        var prices = await ReadPricesAsync(owner, venue.Id);
        var monday = prices.Bands.Where(band => band.Day == nameof(DayOfWeek.Monday)).ToArray();
        Assert.Equal([6, 18], monday.Select(band => band.FromHour));
        Assert.Equal([200m, 300m], monday.Select(band => band.BahtPerHour));
    }

    [Fact]
    public async Task Publishing_again_replaces_what_a_new_booking_would_pay_and_keeps_the_old_list()
    {
        var (owner, venue) = await OpenVenueAsync();
        var first = await SetPricesAsync(owner, venue.Id, AllWeek(6, 22, 200m));

        var second = await SetPricesAsync(owner, venue.Id, AllWeek(6, 22, 250m));

        var inForce = await ReadPricesAsync(owner, venue.Id);
        Assert.Equal(second.Id, inForce.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.All(inForce.Bands, band => Assert.Equal(250m, band.BahtPerHour));
    }

    [Fact]
    public async Task Two_bands_may_not_cover_the_same_hour()
    {
        var (owner, venue) = await OpenVenueAsync();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/prices",
            new SetPricesRequest([.. AllWeek(6, 22, 200m), .. AllWeek(20, 24, 300m)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PricingErrorCodes.OverlappingBands, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task An_open_hour_with_no_price_is_refused()
    {
        var (owner, venue) = await OpenVenueAsync();

        // The venue is open 6–22; these bands stop at 20.
        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/prices", new SetPricesRequest([.. AllWeek(6, 20, 200m)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PricingErrorCodes.HourWithoutPrice, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_closed_day_needs_no_price()
    {
        var (owner, venue) = await OpenVenueAsync(closedOn: DayOfWeek.Monday);

        var prices = await SetPricesAsync(
            owner, venue.Id, AllWeek(6, 22, 200m).Where(band => band.Day != nameof(DayOfWeek.Monday)));

        Assert.DoesNotContain(prices.Bands, band => band.Day == nameof(DayOfWeek.Monday));
    }

    [Fact]
    public async Task Prices_cannot_be_set_before_the_venue_says_when_it_is_open()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/prices", new SetPricesRequest([.. AllWeek(6, 22, 200m)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PricingErrorCodes.NoOpeningHours, await response.ErrorCodeAsync());
    }

    [Theory]
    [InlineData(0, PricingErrorCodes.InvalidPrice)]
    [InlineData(-50, PricingErrorCodes.InvalidPrice)]
    [InlineData(200_000, PricingErrorCodes.InvalidPrice)]
    public async Task A_price_that_makes_no_sense_is_refused(decimal baht, string expected)
    {
        var (owner, venue) = await OpenVenueAsync();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/prices", new SetPricesRequest([.. AllWeek(6, 22, baht)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expected, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_price_with_more_than_satang_is_refused()
    {
        var (owner, venue) = await OpenVenueAsync();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/prices", new SetPricesRequest([.. AllWeek(6, 22, 200.123m)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PricingErrorCodes.InvalidPrice, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Satang_survive_the_round_trip()
    {
        var (owner, venue) = await OpenVenueAsync();

        await SetPricesAsync(owner, venue.Id, AllWeek(6, 22, 249.50m));

        Assert.All((await ReadPricesAsync(owner, venue.Id)).Bands,
            band => Assert.Equal(249.50m, band.BahtPerHour));
    }

    [Fact]
    public async Task Staff_read_the_prices_but_only_ManageSettings_publishes_them()
    {
        var (owner, venue) = await OpenVenueAsync();
        await SetPricesAsync(owner, venue.Id, AllWeek(6, 22, 200m));
        var staff = await scenario.StaffClientAsync(owner, venue.Id, nameof(VenuePermissions.VerifySlip));

        Assert.NotEmpty((await ReadPricesAsync(staff, venue.Id)).Bands);

        var refused = await staff.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/prices", new SetPricesRequest([.. AllWeek(6, 22, 100m)]));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task An_outsider_reaches_neither_the_prices_nor_the_policy()
    {
        var (_, venue) = await OpenVenueAsync();
        var outsider = await scenario.SignedInClientAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/venues/{venue.Id}/prices")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await outsider.GetAsync($"/api/venues/{venue.Id}/cancellation-policy")).StatusCode);
    }

    [Fact]
    public async Task A_venue_that_never_set_a_policy_still_has_the_one_it_started_with()
    {
        var (owner, venue) = await OpenVenueAsync();

        var policy = await ReadPolicyAsync(owner, venue.Id);

        var tier = Assert.Single(policy.Tiers);
        Assert.Equal(24, tier.HoursBefore);
        Assert.Equal(100, tier.RefundPercent);
    }

    [Fact]
    public async Task A_policy_comes_back_most_generous_first()
    {
        var (owner, venue) = await OpenVenueAsync();

        await SetPolicyAsync(owner, venue.Id, [new(2, 0), new(48, 100), new(12, 50)]);

        var policy = await ReadPolicyAsync(owner, venue.Id);
        Assert.Equal([48, 12, 2], policy.Tiers.Select(tier => tier.HoursBefore));
        Assert.Equal([100, 50, 0], policy.Tiers.Select(tier => tier.RefundPercent));
    }

    [Fact]
    public async Task Cancelling_earlier_can_never_get_you_less_back()
    {
        var (owner, venue) = await OpenVenueAsync();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/cancellation-policy",
            new SetCancellationPolicyRequest([new(24, 50), new(48, 25)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PricingErrorCodes.TiersNotInOrder, await response.ErrorCodeAsync());
    }

    [Theory]
    [InlineData(0, 0, PricingErrorCodes.NoTiers)]
    [InlineData(4, 100, PricingErrorCodes.TooManyTiers)]
    public async Task A_policy_with_the_wrong_number_of_steps_is_refused(
        int tiers,
        int percent,
        string expected)
    {
        var (owner, venue) = await OpenVenueAsync();
        var steps = Enumerable.Range(1, tiers)
            .Select(step => new CancellationTierRequest(step * 12, percent))
            .ToArray();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/cancellation-policy", new SetCancellationPolicyRequest(steps));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expected, await response.ErrorCodeAsync());
    }

    [Theory]
    [InlineData(-1, 100, PricingErrorCodes.InvalidTier)]
    [InlineData(24, 101, PricingErrorCodes.InvalidTier)]
    [InlineData(24, -1, PricingErrorCodes.InvalidTier)]
    public async Task A_step_that_makes_no_sense_is_refused(int hours, int percent, string expected)
    {
        var (owner, venue) = await OpenVenueAsync();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/cancellation-policy",
            new SetCancellationPolicyRequest([new(hours, percent)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expected, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task The_same_notice_cannot_be_named_twice()
    {
        var (owner, venue) = await OpenVenueAsync();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/cancellation-policy",
            new SetCancellationPolicyRequest([new(24, 100), new(24, 50)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(PricingErrorCodes.DuplicateTier, await response.ErrorCodeAsync());
    }

    [Theory]
    [InlineData(72, 100)] // Earlier than every step: the most generous one applies.
    [InlineData(48, 100)]
    [InlineData(24, 50)]
    [InlineData(13, 25)] // 13 hours is short of the 24-hour step, so the 12-hour one applies.
    [InlineData(12, 25)]
    [InlineData(11, 0)] // Later than every step: nothing back.
    public void A_cancellation_gets_the_best_step_it_qualifies_for(int hoursBefore, int expected)
    {
        var policy = CancellationPolicy.Create(
            Guid.NewGuid(),
            [new(48, 100), new(24, 50), new(12, 25)],
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);

        Assert.Equal(expected, policy.RefundPercentFor(hoursBefore));
    }

    private static PriceBandRequest[] AllWeek(int from, int to, decimal baht) =>
        Enum.GetValues<DayOfWeek>()
            .Select(day => new PriceBandRequest(day.ToString(), from, to, baht))
            .ToArray();

    /// <summary>A venue that is open, because prices are checked against the hours it sells.</summary>
    private async Task<(HttpClient Owner, VenueResponse Venue)> OpenVenueAsync(DayOfWeek? closedOn = null)
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);

        var week = Enum.GetValues<DayOfWeek>()
            .Select(day => day == closedOn
                ? new OpeningHoursDayRequest(day.ToString(), null, null)
                : new OpeningHoursDayRequest(day.ToString(), 6, 22))
            .ToArray();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/opening-hours",
            new SetOpeningHoursRequest(PlatformToday, week));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (owner, venue);
    }

    private static DateOnly PlatformToday =>
        CourtBooking.Api.Localization.PlatformRequirements.BangkokToday(TimeProvider.System);

    private static async Task<PriceListResponse> SetPricesAsync(
        HttpClient client,
        Guid venueId,
        IEnumerable<PriceBandRequest> bands) =>
        await VenueScenario.ReadAsync<PriceListResponse>(
            await client.PutAsJsonAsync($"/api/venues/{venueId}/prices", new SetPricesRequest(bands.ToArray())));

    private static async Task<PriceListResponse> ReadPricesAsync(HttpClient client, Guid venueId) =>
        await VenueScenario.ReadAsync<PriceListResponse>(
            await client.GetAsync($"/api/venues/{venueId}/prices"));

    private static async Task<CancellationPolicyResponse> SetPolicyAsync(
        HttpClient client,
        Guid venueId,
        CancellationTierRequest[] tiers) =>
        await VenueScenario.ReadAsync<CancellationPolicyResponse>(
            await client.PutAsJsonAsync(
                $"/api/venues/{venueId}/cancellation-policy", new SetCancellationPolicyRequest(tiers)));

    private static async Task<CancellationPolicyResponse> ReadPolicyAsync(HttpClient client, Guid venueId) =>
        await VenueScenario.ReadAsync<CancellationPolicyResponse>(
            await client.GetAsync($"/api/venues/{venueId}/cancellation-policy"));
}
