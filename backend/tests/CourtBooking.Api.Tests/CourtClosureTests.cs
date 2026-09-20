using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>Shutting a court for a stretch of time: PRD US-11, US-14, 6.2.</summary>
public sealed class CourtClosureTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    private static readonly DateOnly Tomorrow = VenueScenario.Today.AddDays(1);

    [Fact]
    public async Task A_closed_stretch_stops_being_on_sale_and_the_rest_of_the_day_does_not()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        await ClosedAsync(owner, venue.Id, courts[0], 18, 20, "ซ่อมพื้น");

        var day = await scenario.ReadAvailabilityAsync(owner, venue.Id, Tomorrow);
        var court = Assert.Single(day.Courts, one => one.CourtId == courts[0]);

        Assert.Equal(nameof(HourStatus.Closed), Hour(court, 18).Status);
        Assert.Equal(nameof(HourStatus.Closed), Hour(court, 19).Status);

        // The hour the closure ends on is the first hour the court is back.
        Assert.Equal(nameof(HourStatus.Free), Hour(court, 20).Status);
        Assert.Equal(nameof(HourStatus.Free), Hour(court, 17).Status);
    }

    [Fact]
    public async Task Closing_one_court_leaves_the_others_alone()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);

        await ClosedAsync(owner, venue.Id, courts[0], 18, 20, "ไฟดับฝั่งซ้าย");

        var day = await scenario.ReadAvailabilityAsync(owner, venue.Id, Tomorrow);
        Assert.Equal(
            nameof(HourStatus.Closed),
            Hour(Assert.Single(day.Courts, one => one.CourtId == courts[0]), 18).Status);
        Assert.Equal(
            nameof(HourStatus.Free),
            Hour(Assert.Single(day.Courts, one => one.CourtId == courts[1]), 18).Status);
    }

    /// <summary>
    /// The grid and the write that takes an hour read the same model, so a shut hour cannot be
    /// bought behind the grid's back (PRD 6.2, BR-04).
    /// </summary>
    [Fact]
    public async Task A_shut_hour_cannot_be_booked_even_by_asking_directly()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        await ClosedAsync(owner, venue.Id, courts[0], 18, 20, "ซ่อมพื้น");

        var booker = await scenario.SignedInClientAsync();
        var refused = await booker.PostAsJsonAsync(
            "/api/bookings",
            new CreateBookingRequest(
                venue.Id, [new BookingSlotRequest(courts[0], Tomorrow, 18)]));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // Not on sale, rather than taken by somebody else: the difference is what the booker is
        // told, and a shut court has nobody in it.
        Assert.Equal(BookingErrorCodes.HourNotAvailable, await refused.ErrorCodeAsync());
    }

    /// <summary>
    /// The rule of this story: the venue is shown what stands in the way and decides what happens
    /// to those bookings itself, because cancelling one decides what goes back (PRD 6.1, BR-06).
    /// </summary>
    [Fact]
    public async Task A_court_with_bookings_inside_the_stretch_will_not_close()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));

        var refused = await CloseAsync(owner, venue.Id, courts[0], 17, 21, "ซ่อมพื้น");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(CourtErrorCodes.BookingsInTheWay, await refused.ErrorCodeAsync());

        var clash = Assert.Single(await ClashesAsync(refused));
        Assert.Equal(booking.Id, clash.BookingId);
        Assert.Equal(courts[0], clash.CourtId);
        Assert.Equal(Tomorrow, clash.Date);
        Assert.Equal(18, clash.FromHour);
        Assert.Equal(19, clash.ToHour);

        // Nothing was written: the court is still on sale for the whole stretch.
        var day = await scenario.ReadAvailabilityAsync(owner, venue.Id, Tomorrow);
        Assert.Equal(
            nameof(HourStatus.Free),
            Hour(Assert.Single(day.Courts, one => one.CourtId == courts[0]), 17).Status);
    }

    [Fact]
    public async Task Once_the_bookings_are_out_of_the_way_the_court_closes()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await CloseAsync(owner, venue.Id, courts[0], 17, 21, "ซ่อมพื้น")).StatusCode);

        await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null);

        var closed = await CloseAsync(owner, venue.Id, courts[0], 17, 21, "ซ่อมพื้น");
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
    }

    /// <summary>
    /// A hold that ran out is in nobody's way — the same definition of "taken" the grid uses
    /// decides this too (PRD 9.2).
    /// </summary>
    [Fact]
    public async Task A_hold_that_ran_out_does_not_stand_in_the_way()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));

        await scenario.LapseHoldAsync(booking.Id);

        var closed = await CloseAsync(owner, venue.Id, courts[0], 17, 21, "ซ่อมพื้น");
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
    }

    [Fact]
    public async Task Lifting_a_closure_puts_the_hours_back_and_cannot_be_done_twice()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var closure = await ClosedAsync(owner, venue.Id, courts[0], 18, 20, "ซ่อมพื้น");

        var lifted = await owner.PostAsync(
            $"/api/venues/{venue.Id}/closures/{closure.Id}/lift", null);
        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        Assert.NotNull((await VenueScenario.ReadAsync<CourtClosureResponse>(lifted)).LiftedAt);

        var day = await scenario.ReadAvailabilityAsync(owner, venue.Id, Tomorrow);
        Assert.Equal(
            nameof(HourStatus.Free),
            Hour(Assert.Single(day.Courts, one => one.CourtId == courts[0]), 18).Status);

        var again = await owner.PostAsync(
            $"/api/venues/{venue.Id}/closures/{closure.Id}/lift", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(CourtErrorCodes.ClosureAlreadyLifted, await again.ErrorCodeAsync());
    }

    /// <summary>
    /// A lifted closure stays on the list: why an evening had no bookings is part of the venue's
    /// history, and deleting the row would lose it (PRD US-15).
    /// </summary>
    [Fact]
    public async Task A_lifted_closure_is_still_listed()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var closure = await ClosedAsync(owner, venue.Id, courts[0], 18, 20, "ซ่อมพื้น");
        await owner.PostAsync($"/api/venues/{venue.Id}/closures/{closure.Id}/lift", null);

        var listed = await VenueScenario.ReadAsync<CourtClosureResponse[]>(
            await owner.GetAsync($"/api/venues/{venue.Id}/closures"));

        var seen = Assert.Single(listed, one => one.Id == closure.Id);
        Assert.NotNull(seen.LiftedAt);
        Assert.Equal("ซ่อมพื้น", seen.Reason);
    }

    [Theory]
    [InlineData(20, 18)]
    [InlineData(18, 18)]
    [InlineData(-1, 18)]
    [InlineData(18, 25)]
    public async Task A_stretch_that_is_not_a_stretch_of_hours_is_refused(int from, int to)
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        var refused = await CloseAsync(owner, venue.Id, courts[0], from, to, "ซ่อมพื้น");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(CourtErrorCodes.InvalidClosureRange, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_stretch_that_has_already_been_and_gone_is_refused()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var lastWeek = VenueScenario.Today.AddDays(-7);

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/courts/{courts[0]}/closures",
            new CloseCourtRequest(lastWeek, 18, lastWeek, 20, "ซ่อมพื้น"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(CourtErrorCodes.InvalidClosureRange, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_court_shut_for_no_stated_reason_is_refused()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        var refused = await CloseAsync(owner, venue.Id, courts[0], 18, 20, "   ");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(CourtErrorCodes.InvalidClosureReason, await refused.ErrorCodeAsync());
    }

    /// <summary>
    /// Closing a court is its own permission, held by staff by default while the rest of the
    /// venue's settings are not (PRD US-14).
    /// </summary>
    [Fact]
    public async Task Closing_takes_the_permission_for_it_and_nothing_else_does()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        var closer = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.CloseCourt));
        Assert.Equal(
            HttpStatusCode.OK,
            (await CloseAsync(closer, venue.Id, courts[0], 18, 20, "ซ่อมพื้น")).StatusCode);

        var settings = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageSettings));
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await CloseAsync(settings, venue.Id, courts[0], 20, 21, "ซ่อมพื้น")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await settings.GetAsync($"/api/venues/{venue.Id}/closures")).StatusCode);
    }

    [Fact]
    public async Task Another_venue_cannot_shut_this_one_s_court()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (stranger, elsewhere, _) = await scenario.BookableVenueAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await CloseAsync(stranger, elsewhere.Id, courts[0], 18, 20, "ซ่อมพื้น")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await CloseAsync(stranger, venue.Id, courts[0], 18, 20, "ซ่อมพื้น")).StatusCode);
    }

    /// <summary>
    /// The list of what is in the way names bookings, not bookers: who booked is behind
    /// <c>ManageBookings</c>, and this screen is behind <c>CloseCourt</c> (PDPA, PRD 8, US-14).
    /// </summary>
    [Fact]
    public async Task What_stands_in_the_way_does_not_name_the_booker()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, email) = await scenario.SignedInClientWithEmailAsync();
        await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));

        var refused = await CloseAsync(owner, venue.Id, courts[0], 17, 21, "ซ่อมพื้น");

        Assert.DoesNotContain(email, await refused.Content.ReadAsStringAsync());
    }

    private static HourResponse Hour(CourtAvailabilityResponse court, int hour) =>
        Assert.Single(court.Hours, one => one.Hour == hour);

    private static Task<HttpResponseMessage> CloseAsync(
        HttpClient client,
        Guid venueId,
        Guid courtId,
        int fromHour,
        int toHour,
        string reason) =>
        client.PostAsJsonAsync(
            $"/api/venues/{venueId}/courts/{courtId}/closures",
            new CloseCourtRequest(Tomorrow, fromHour, Tomorrow, toHour, reason));

    private static async Task<CourtClosureResponse> ClosedAsync(
        HttpClient client,
        Guid venueId,
        Guid courtId,
        int fromHour,
        int toHour,
        string reason) =>
        await VenueScenario.ReadAsync<CourtClosureResponse>(
            await CloseAsync(client, venueId, courtId, fromHour, toHour, reason));

    private static async Task<ClashingBookingResponse[]> ClashesAsync(HttpResponseMessage refused)
    {
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("bookings").Deserialize<ClashingBookingResponse[]>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
