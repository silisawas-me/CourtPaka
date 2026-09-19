using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>
/// What someone who has not signed in can see: the venues, and when their courts are free
/// (PRD US-02). Nothing here needs a session, and nothing here names another booker.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PublicVenueTests(ApiTestFixture api)
{
    private readonly VenueScenario scenario = new(api);

    private static DateOnly Today => PlatformRequirements.BangkokToday(TimeProvider.System);

    [Fact]
    public async Task A_booker_who_has_not_signed_in_can_read_the_grid()
    {
        var (_, venue) = await BookableVenueAsync();
        var anonymous = api.CreateClient();

        var day = await ReadAvailabilityAsync(anonymous, venue.Id, Today);

        var court = Assert.Single(day.Courts);
        Assert.Equal(16, court.Hours.Length); // 06:00 to 22:00.
        Assert.All(court.Hours, hour => Assert.Equal(nameof(HourStatus.Free), hour.Status));
        Assert.All(court.Hours, hour => Assert.Equal(200m, hour.BahtPerHour));
    }

    [Fact]
    public async Task A_venue_that_is_not_approved_is_invisible_to_a_booker()
    {
        var (owner, venue) = await BookableVenueAsync();
        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);
        var anonymous = api.CreateClient();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/api/venues/{venue.Id}/public")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await anonymous.GetAsync($"/api/venues/{venue.Id}/availability")).StatusCode);
        Assert.DoesNotContain(await SearchAsync(anonymous, venue.Code), found => found.Id == venue.Id);

        // Its own people still reach it; it is read-only, not hidden from them (PRD US-20).
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/venues/{venue.Id}")).StatusCode);
    }

    [Fact]
    public async Task Search_finds_a_venue_by_name_district_or_province()
    {
        var (_, venue) = await BookableVenueAsync();
        await scenario.SetStatusAsync(venue.Id, VenueStatus.Approved);
        var anonymous = api.CreateClient();

        foreach (var term in new[] { venue.Name, venue.District, venue.Province })
        {
            Assert.Contains(await SearchAsync(anonymous, term), found => found.Id == venue.Id);
        }
    }

    [Fact]
    public async Task Search_ignores_the_case_of_what_was_typed()
    {
        var (_, venue) = await BookableVenueAsync();
        var anonymous = api.CreateClient();

        Assert.Contains(
            await SearchAsync(anonymous, venue.Name.ToUpperInvariant()), found => found.Id == venue.Id);
        Assert.Contains(
            await SearchAsync(anonymous, venue.Name.ToLowerInvariant()), found => found.Id == venue.Id);
    }

    [Fact]
    public async Task A_day_the_venue_is_closed_has_no_hours_at_all()
    {
        var (owner, venue) = await BookableVenueAsync();
        var closedDay = Today.AddDays(1);
        await PublishWeekAsync(owner, venue.Id, Today, closedOn: closedDay.DayOfWeek);
        var anonymous = api.CreateClient();

        var day = await ReadAvailabilityAsync(anonymous, venue.Id, closedDay);

        Assert.Null(day.OpensHour);
        Assert.Empty(Assert.Single(day.Courts).Hours);
    }

    [Fact]
    public async Task A_court_taken_out_of_use_shows_as_closed_from_that_day_on()
    {
        var (owner, venue) = await BookableVenueAsync();
        var court = Assert.Single(await CourtsAsync(owner, venue.Id));
        var friday = Today.AddDays(5);

        var status = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/courts/{court.Id}/status",
            new ChangeCourtStatusRequest(false, friday));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);

        var anonymous = api.CreateClient();
        Assert.All(
            Assert.Single((await ReadAvailabilityAsync(anonymous, venue.Id, friday.AddDays(-1))).Courts).Hours,
            hour => Assert.Equal(nameof(HourStatus.Free), hour.Status));
        Assert.All(
            Assert.Single((await ReadAvailabilityAsync(anonymous, venue.Id, friday)).Courts).Hours,
            hour => Assert.Equal(nameof(HourStatus.Closed), hour.Status));
    }

    [Fact]
    public async Task A_closed_hour_carries_no_price()
    {
        var (owner, venue) = await BookableVenueAsync();
        var court = Assert.Single(await CourtsAsync(owner, venue.Id));
        await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/courts/{court.Id}/status", new ChangeCourtStatusRequest(false));

        var day = await ReadAvailabilityAsync(api.CreateClient(), venue.Id, Today);

        Assert.All(Assert.Single(day.Courts).Hours, hour => Assert.Null(hour.BahtPerHour));
    }

    [Fact]
    public async Task The_grid_reads_the_week_in_force_on_the_day_asked_about()
    {
        var (owner, venue) = await BookableVenueAsync();
        var fromNextWeek = Today.AddDays(7);
        // Longer days from next week, and the prices that cover them.
        await SetPricesAsync(owner, venue.Id, 6, 24, 200m);
        await PublishWeekAsync(owner, venue.Id, fromNextWeek, opens: 6, closes: 24);

        var anonymous = api.CreateClient();
        Assert.Equal(22, (await ReadAvailabilityAsync(anonymous, venue.Id, Today)).ClosesHour);
        Assert.Equal(24, (await ReadAvailabilityAsync(anonymous, venue.Id, fromNextWeek)).ClosesHour);
    }

    [Theory]
    [InlineData(-1, AvailabilityErrorCodes.DateInThePast)]
    [InlineData(31, AvailabilityErrorCodes.DateTooFarAhead)]
    public async Task A_date_outside_the_booking_window_is_refused(int daysFromToday, string expected)
    {
        var (_, venue) = await BookableVenueAsync();
        var anonymous = api.CreateClient();

        var response = await anonymous.GetAsync(
            $"/api/venues/{venue.Id}/availability?date={Today.AddDays(daysFromToday):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expected, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task The_last_bookable_day_is_thirty_days_out()
    {
        var (_, venue) = await BookableVenueAsync();
        var anonymous = api.CreateClient();

        var day = await ReadAvailabilityAsync(anonymous, venue.Id, Today.AddDays(30));

        Assert.Equal(Today.AddDays(Availability.BookableDaysAhead), day.Date);
    }

    /// <summary>A venue a booker could actually use: approved, with a court, hours and prices.</summary>
    private async Task<(HttpClient Owner, PublicVenueResponse Venue)> BookableVenueAsync()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        await PublishWeekAsync(owner, venue.Id, Today);
        await SetPricesAsync(owner, venue.Id, 6, 22, 200m);

        var court = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/courts", new CreateCourtRequest("Court 1"));
        Assert.Equal(HttpStatusCode.Created, court.StatusCode);

        // Venue approval arrives with US-20; until then the test sets the status directly.
        await scenario.SetStatusAsync(venue.Id, VenueStatus.Approved);

        return (owner, new PublicVenueResponse(
            venue.Id, venue.Code, venue.Name, venue.AddressLine, venue.District, venue.Province));
    }

    private static async Task PublishWeekAsync(
        HttpClient owner,
        Guid venueId,
        DateOnly from,
        int opens = 6,
        int closes = 22,
        DayOfWeek? closedOn = null)
    {
        var week = Enum.GetValues<DayOfWeek>()
            .Select(day => day == closedOn
                ? new OpeningHoursDayRequest(day.ToString(), null, null)
                : new OpeningHoursDayRequest(day.ToString(), opens, closes))
            .ToArray();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venueId}/opening-hours", new SetOpeningHoursRequest(from, week));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task SetPricesAsync(
        HttpClient owner,
        Guid venueId,
        int from,
        int to,
        decimal baht)
    {
        var bands = Enum.GetValues<DayOfWeek>()
            .Select(day => new PriceBandRequest(day.ToString(), from, to, baht))
            .ToArray();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venueId}/prices", new SetPricesRequest(bands));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<CourtResponse[]> CourtsAsync(HttpClient client, Guid venueId) =>
        await VenueScenario.ReadAsync<CourtResponse[]>(
            await client.GetAsync($"/api/venues/{venueId}/courts"));

    private static async Task<AvailabilityResponse> ReadAvailabilityAsync(
        HttpClient client,
        Guid venueId,
        DateOnly date) =>
        await VenueScenario.ReadAsync<AvailabilityResponse>(
            await client.GetAsync($"/api/venues/{venueId}/availability?date={date:yyyy-MM-dd}"));

    private static async Task<PublicVenueResponse[]> SearchAsync(HttpClient client, string term) =>
        await VenueScenario.ReadAsync<PublicVenueResponse[]>(
            await client.GetAsync($"/api/venues/search?q={Uri.EscapeDataString(term)}"));
}
