using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>Courts and opening hours: the setup every booking screen reads from (PRD US-11).</summary>
[Collection(ApiCollection.Name)]
public sealed class CourtSettingsTests(ApiTestFixture api)
{
    private readonly VenueScenario scenario = new(api);

    private static DateOnly Today => PlatformRequirements.BangkokToday(TimeProvider.System);

    [Fact]
    public async Task A_new_court_is_in_use_and_lands_at_the_end_of_the_grid()
    {
        var (owner, venue) = await OwnedVenueAsync();

        var first = await AddCourtAsync(owner, venue.Id, "Court 1");
        var second = await AddCourtAsync(owner, venue.Id, "Court 2");

        Assert.True(first.IsActive);
        Assert.Equal(0, first.Position);
        Assert.Equal(1, second.Position);

        var listed = await ListCourtsAsync(owner, venue.Id);
        Assert.Equal(["Court 1", "Court 2"], listed.Select(court => court.Name));
    }

    [Fact]
    public async Task Two_courts_at_one_venue_cannot_share_a_name()
    {
        var (owner, venue) = await OwnedVenueAsync();
        await AddCourtAsync(owner, venue.Id, "Court 1");

        var response = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/courts", new CreateCourtRequest("Court 1"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(CourtErrorCodes.NameAlreadyUsed, await scenario.ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Another_venue_may_use_the_same_court_name()
    {
        var (owner, venue) = await OwnedVenueAsync();
        var other = await scenario.CreateVenueAsync(owner);

        await AddCourtAsync(owner, venue.Id, "Court 1");
        var second = await AddCourtAsync(owner, other.Id, "Court 1");

        Assert.Equal("Court 1", second.Name);
    }

    [Fact]
    public async Task A_court_without_a_name_is_refused()
    {
        var (owner, venue) = await OwnedVenueAsync();

        var response = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/courts", new CreateCourtRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(CourtErrorCodes.InvalidCourtName, await scenario.ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Taking_a_court_out_of_use_is_recorded_with_the_date_it_happened()
    {
        var (owner, venue) = await OwnedVenueAsync();
        var court = await AddCourtAsync(owner, venue.Id, "Court 1");

        var changed = await ChangeStatusAsync(owner, venue.Id, court.Id, active: false);
        Assert.False(changed.IsActive);

        var history = await HistoryAsync(owner, venue.Id, court.Id);
        // Newest first: out of use today, in use from the day it was added.
        Assert.Equal([false, true], history.Select(entry => entry.Active));
        Assert.All(history, entry => Assert.Equal(Today, entry.EffectiveFrom));
    }

    [Fact]
    public async Task Putting_a_court_back_records_a_second_change()
    {
        var (owner, venue) = await OwnedVenueAsync();
        var court = await AddCourtAsync(owner, venue.Id, "Court 1");

        await ChangeStatusAsync(owner, venue.Id, court.Id, active: false);
        var back = await ChangeStatusAsync(owner, venue.Id, court.Id, active: true);

        Assert.True(back.IsActive);
        Assert.Equal(3, (await HistoryAsync(owner, venue.Id, court.Id)).Length);
    }

    [Fact]
    public async Task Repeating_the_state_a_court_is_already_in_changes_nothing()
    {
        var (owner, venue) = await OwnedVenueAsync();
        var court = await AddCourtAsync(owner, venue.Id, "Court 1");

        var unchanged = await ChangeStatusAsync(owner, venue.Id, court.Id, active: true);

        Assert.True(unchanged.IsActive);
        Assert.Single(await HistoryAsync(owner, venue.Id, court.Id));
    }

    [Fact]
    public async Task A_court_of_another_venue_is_not_found_through_this_one()
    {
        var (owner, venue) = await OwnedVenueAsync();
        var other = await scenario.CreateVenueAsync(owner);
        var court = await AddCourtAsync(owner, other.Id, "Court 1");

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/courts/{court.Id}", new UpdateCourtRequest("Renamed", 0));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Staff_read_the_courts_but_only_ManageSettings_changes_them()
    {
        var (owner, venue) = await OwnedVenueAsync();
        var court = await AddCourtAsync(owner, venue.Id, "Court 1");
        var staff = await scenario.StaffClientAsync(owner, venue.Id, nameof(VenuePermissions.VerifySlip));

        Assert.Single(await ListCourtsAsync(staff, venue.Id));

        var added = await staff.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/courts", new CreateCourtRequest("Court 2"));
        var renamed = await staff.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/courts/{court.Id}", new UpdateCourtRequest("Court 9", 0));
        var status = await staff.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/courts/{court.Id}/status", new ChangeCourtStatusRequest(false));

        Assert.Equal(HttpStatusCode.Forbidden, added.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, renamed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);
    }

    [Fact]
    public async Task Staff_holding_ManageSettings_may_change_them()
    {
        var (owner, venue) = await OwnedVenueAsync();
        var staff = await scenario.StaffClientAsync(owner, venue.Id, nameof(VenuePermissions.ManageSettings));

        var court = await AddCourtAsync(staff, venue.Id, "Court 1");

        Assert.Equal("Court 1", court.Name);
    }

    [Fact]
    public async Task A_suspended_venue_keeps_its_settings_readable_but_unchangeable()
    {
        var (owner, venue) = await OwnedVenueAsync();
        await AddCourtAsync(owner, venue.Id, "Court 1");
        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);

        Assert.Single(await ListCourtsAsync(owner, venue.Id));
        var response = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/courts", new CreateCourtRequest("Court 2"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_outsider_reaches_neither_the_courts_nor_the_opening_hours()
    {
        var (owner, venue) = await OwnedVenueAsync();
        var outsider = await scenario.SignedInClientAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/venues/{venue.Id}/courts")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/venues/{venue.Id}/opening-hours")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await outsider.PutAsJsonAsync(
                $"/api/venues/{venue.Id}/opening-hours", OpenEveryDay(Today))).StatusCode);
    }

    [Fact]
    public async Task The_published_week_comes_back_as_the_one_in_force()
    {
        var (owner, venue) = await OwnedVenueAsync();

        await SetHoursAsync(owner, venue.Id, OpenEveryDay(Today));
        var schedules = await ListHoursAsync(owner, venue.Id);

        var schedule = Assert.Single(schedules);
        Assert.True(schedule.InForce);
        Assert.Equal(7, schedule.Days.Length);
        Assert.Equal(6, schedule.Days.Single(day => day.Day == nameof(DayOfWeek.Monday)).OpensHour);
        Assert.Equal(22, schedule.Days.Single(day => day.Day == nameof(DayOfWeek.Monday)).ClosesHour);
    }

    [Fact]
    public async Task A_week_dated_ahead_is_listed_next_to_the_one_in_force()
    {
        var (owner, venue) = await OwnedVenueAsync();
        await SetHoursAsync(owner, venue.Id, OpenEveryDay(Today));

        var later = OpenEveryDay(Today.AddDays(14), opens: 8, closes: 24);
        await SetHoursAsync(owner, venue.Id, later);

        var schedules = await ListHoursAsync(owner, venue.Id);
        Assert.Equal(2, schedules.Length);
        Assert.True(schedules[0].InForce);
        Assert.False(schedules[1].InForce);
        Assert.Equal(Today.AddDays(14), schedules[1].EffectiveFrom);
    }

    [Fact]
    public async Task Publishing_the_same_start_date_twice_replaces_the_first_version()
    {
        var (owner, venue) = await OwnedVenueAsync();
        var start = Today.AddDays(7);

        await SetHoursAsync(owner, venue.Id, OpenEveryDay(start));
        await SetHoursAsync(owner, venue.Id, OpenEveryDay(start, opens: 9, closes: 21));

        var schedules = await ListHoursAsync(owner, venue.Id);
        var schedule = Assert.Single(schedules, item => item.EffectiveFrom == start);
        Assert.Equal(9, schedule.Days.First().OpensHour);
    }

    [Fact]
    public async Task A_closed_weekday_is_kept_as_closed_rather_than_dropped()
    {
        var (owner, venue) = await OwnedVenueAsync();

        var week = OpenEveryDay(Today).Days
            .Select(day => day.Day == nameof(DayOfWeek.Monday) ? day with { OpensHour = null, ClosesHour = null } : day)
            .ToArray();
        await SetHoursAsync(owner, venue.Id, new SetOpeningHoursRequest(Today, week));

        var schedule = Assert.Single(await ListHoursAsync(owner, venue.Id));
        var monday = schedule.Days.Single(day => day.Day == nameof(DayOfWeek.Monday));
        Assert.Null(monday.OpensHour);
        Assert.Null(monday.ClosesHour);
        Assert.Equal(7, schedule.Days.Length);
    }

    [Theory]
    [InlineData(22, 6, CourtErrorCodes.InvalidHours)] // Closes before it opens.
    [InlineData(6, 25, CourtErrorCodes.InvalidHours)] // Past midnight at the end of the day.
    [InlineData(-1, 22, CourtErrorCodes.InvalidHours)]
    [InlineData(6, 6, CourtErrorCodes.InvalidHours)] // Open for no hours at all.
    public async Task Hours_that_describe_no_open_time_are_refused(int opens, int closes, string expected)
    {
        var (owner, venue) = await OwnedVenueAsync();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/opening-hours", OpenEveryDay(Today, opens, closes));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expected, await scenario.ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task A_week_missing_a_day_is_refused_rather_than_read_as_closed()
    {
        var (owner, venue) = await OwnedVenueAsync();
        var week = OpenEveryDay(Today).Days.Where(day => day.Day != nameof(DayOfWeek.Sunday)).ToArray();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/opening-hours", new SetOpeningHoursRequest(Today, week));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(CourtErrorCodes.MissingDay, await scenario.ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task A_day_named_twice_is_refused()
    {
        var (owner, venue) = await OwnedVenueAsync();
        var week = OpenEveryDay(Today).Days
            .Select(day => day.Day == nameof(DayOfWeek.Sunday) ? day with { Day = nameof(DayOfWeek.Monday) } : day)
            .ToArray();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/opening-hours", new SetOpeningHoursRequest(Today, week));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(CourtErrorCodes.DuplicateDay, await scenario.ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task A_week_that_never_opens_is_refused()
    {
        var (owner, venue) = await OwnedVenueAsync();
        var week = OpenEveryDay(Today).Days
            .Select(day => day with { OpensHour = null, ClosesHour = null })
            .ToArray();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/opening-hours", new SetOpeningHoursRequest(Today, week));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(CourtErrorCodes.NeverOpen, await scenario.ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Opening_hours_cannot_be_backdated()
    {
        var (owner, venue) = await OwnedVenueAsync();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/opening-hours", OpenEveryDay(Today.AddDays(-1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(CourtErrorCodes.EffectiveDateInThePast, await scenario.ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_unknown_weekday_is_refused()
    {
        var (owner, venue) = await OwnedVenueAsync();
        var week = OpenEveryDay(Today).Days
            .Select(day => day.Day == nameof(DayOfWeek.Sunday) ? day with { Day = "Funday" } : day)
            .ToArray();

        var response = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/opening-hours", new SetOpeningHoursRequest(Today, week));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(CourtErrorCodes.InvalidDay, await scenario.ReadErrorCodeAsync(response));
    }

    private static SetOpeningHoursRequest OpenEveryDay(DateOnly from, int? opens = 6, int? closes = 22) =>
        new(
            from,
            Enum.GetValues<DayOfWeek>()
                .Select(day => new OpeningHoursDayRequest(day.ToString(), opens, closes))
                .ToArray());

    private async Task<(HttpClient Owner, VenueResponse Venue)> OwnedVenueAsync()
    {
        var owner = await scenario.SignedInClientAsync();
        return (owner, await scenario.CreateVenueAsync(owner));
    }

    private static async Task<CourtResponse> AddCourtAsync(HttpClient client, Guid venueId, string name)
    {
        var response = await client.PostAsJsonAsync($"/api/venues/{venueId}/courts", new CreateCourtRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CourtResponse>())!;
    }

    private static async Task<CourtResponse[]> ListCourtsAsync(HttpClient client, Guid venueId)
    {
        var response = await client.GetAsync($"/api/venues/{venueId}/courts");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CourtResponse[]>())!;
    }

    private static async Task<CourtResponse> ChangeStatusAsync(
        HttpClient client,
        Guid venueId,
        Guid courtId,
        bool active)
    {
        var response = await client.PutAsJsonAsync(
            $"/api/venues/{venueId}/courts/{courtId}/status", new ChangeCourtStatusRequest(active));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CourtResponse>())!;
    }

    private static async Task<CourtStatusChangeResponse[]> HistoryAsync(
        HttpClient client,
        Guid venueId,
        Guid courtId)
    {
        var response = await client.GetAsync($"/api/venues/{venueId}/courts/{courtId}/status-history");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CourtStatusChangeResponse[]>())!;
    }

    private static async Task SetHoursAsync(HttpClient client, Guid venueId, SetOpeningHoursRequest request)
    {
        var response = await client.PutAsJsonAsync($"/api/venues/{venueId}/opening-hours", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<OpeningHoursResponse[]> ListHoursAsync(HttpClient client, Guid venueId)
    {
        var response = await client.GetAsync($"/api/venues/{venueId}/opening-hours");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OpeningHoursResponse[]>())!;
    }
}
