using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>Taking court-hours: PRD US-03, BR-02, BR-04, BR-05, S-22, S-25.</summary>
public sealed class BookingTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task A_booker_holds_the_hours_they_picked_at_the_prices_on_the_grid()
    {
        var (_, venue, courtId) = await BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();

        var booking = await HoldAsync(booker, venue.Id, (courtId, Tomorrow, 18), (courtId, Tomorrow, 19));

        Assert.Equal(nameof(BookingStatus.Held), booking.Status);
        Assert.Equal(venue.Name, booking.VenueName);
        Assert.Equal(400m, booking.TotalBaht);
        Assert.Equal([18, 19], booking.Slots.Select(slot => slot.Hour));
        Assert.All(booking.Slots, slot => Assert.Equal(200m, slot.BahtPerHour));
        Assert.All(booking.Slots, slot => Assert.Equal("Court 1", slot.CourtName));
    }

    [Fact]
    public async Task The_hold_lapses_fifteen_minutes_after_it_was_made()
    {
        var (_, venue, courtId) = await BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();

        var booking = await HoldAsync(booker, venue.Id, (courtId, Tomorrow, 18));

        Assert.Equal(Booking.HoldFor, booking.HoldExpiresAt - booking.CreatedAt);
    }

    [Fact]
    public async Task Each_hour_is_priced_by_the_band_that_covers_it()
    {
        var (owner, venue, courtId) = await BookableVenueAsync();
        // Mornings 200, evenings 300, so a booking across the two is not one price twice.
        await VenueScenario.SetPricesAsync(
            owner,
            venue.Id,
            [.. VenueScenario.AllWeek(6, 18, 200m), .. VenueScenario.AllWeek(18, 22, 300m)]);
        var booker = await scenario.SignedInClientAsync();

        var booking = await HoldAsync(booker, venue.Id, (courtId, Tomorrow, 17), (courtId, Tomorrow, 18));

        Assert.Equal([200m, 300m], booking.Slots.Select(slot => slot.BahtPerHour));
        Assert.Equal(500m, booking.TotalBaht);
    }

    [Fact]
    public async Task An_hour_someone_else_holds_is_booked_on_the_grid_and_refused()
    {
        var (_, venue, courtId) = await BookableVenueAsync();
        var first = await scenario.SignedInClientAsync();
        await HoldAsync(first, venue.Id, (courtId, Tomorrow, 18));

        var second = await scenario.SignedInClientAsync();
        var day = await scenario.ReadAvailabilityAsync(api.CreateClient(), venue.Id, Tomorrow);

        var hour = day.Courts.Single(court => court.CourtId == courtId).Hours.Single(cell => cell.Hour == 18);
        Assert.Equal(nameof(HourStatus.Booked), hour.Status);
        // The price stays: the grid reads as a price list, and it says nothing about who holds it.
        Assert.Equal(200m, hour.BahtPerHour);

        var refused = await PostAsync(second, venue.Id, (courtId, Tomorrow, 18));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.SlotJustTaken, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_booking_that_cannot_take_every_hour_takes_none()
    {
        var (_, venue, courtId) = await BookableVenueAsync();
        var first = await scenario.SignedInClientAsync();
        await HoldAsync(first, venue.Id, (courtId, Tomorrow, 19));

        var second = await scenario.SignedInClientAsync();
        var refused = await PostAsync(
            second, venue.Id, (courtId, Tomorrow, 18), (courtId, Tomorrow, 19));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // The hour that was free is still free: the whole booking was refused (PRD BR-04).
        var day = await scenario.ReadAvailabilityAsync(api.CreateClient(), venue.Id, Tomorrow);
        var hour = day.Courts.Single(court => court.CourtId == courtId).Hours.Single(cell => cell.Hour == 18);
        Assert.Equal(nameof(HourStatus.Free), hour.Status);
    }

    [Fact]
    public async Task Two_bookers_confirming_the_same_hour_at_once_leave_exactly_one_holding_it()
    {
        var (_, venue, courtId) = await BookableVenueAsync();
        var bookers = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => scenario.SignedInClientAsync()));

        var answers = await Task.WhenAll(
            bookers.Select(booker => PostAsync(booker, venue.Id, (courtId, Tomorrow, 20))));

        Assert.Single(answers, answer => answer.StatusCode == HttpStatusCode.Created);
        foreach (var refused in answers.Where(answer => answer.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal(BookingErrorCodes.SlotJustTaken, await refused.ErrorCodeAsync());
        }
    }

    [Fact]
    public async Task A_hold_that_lapsed_puts_its_hours_back_on_sale()
    {
        var (_, venue, courtId) = await BookableVenueAsync();
        var first = await scenario.SignedInClientAsync();
        var abandoned = await HoldAsync(first, venue.Id, (courtId, Tomorrow, 18));
        await scenario.LapseHoldAsync(abandoned.Id);

        // The grid says it is free, so the booking that follows has to agree (PRD 9.2).
        var day = await scenario.ReadAvailabilityAsync(api.CreateClient(), venue.Id, Tomorrow);
        var hour = day.Courts.Single(court => court.CourtId == courtId)
            .Hours.Single(cell => cell.Hour == 18);
        Assert.Equal(nameof(HourStatus.Free), hour.Status);

        var second = await scenario.SignedInClientAsync();
        var taken = await HoldAsync(second, venue.Id, (courtId, Tomorrow, 18));
        Assert.Equal(nameof(BookingStatus.Held), taken.Status);
    }

    [Fact]
    public async Task A_hold_that_lapsed_does_not_stop_its_own_booker_making_another()
    {
        var (_, venue, courtId) = await BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var abandoned = await HoldAsync(booker, venue.Id, (courtId, Tomorrow, 18));
        await scenario.LapseHoldAsync(abandoned.Id);

        var again = await HoldAsync(booker, venue.Id, (courtId, Tomorrow, 19));

        Assert.Equal(nameof(BookingStatus.Held), again.Status);
    }

    [Fact]
    public async Task A_booker_holds_one_booking_at_a_time()
    {
        var (_, venue, courtId) = await BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        await HoldAsync(booker, venue.Id, (courtId, Tomorrow, 18));

        var refused = await PostAsync(booker, venue.Id, (courtId, Tomorrow, 19));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.AlreadyHolding, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task An_hour_starting_within_half_an_hour_cannot_be_taken()
    {
        var (_, venue, courtId) = await BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var (today, hour) = PlatformRequirements.BangkokDateAndHour(DateTimeOffset.UtcNow);

        // The hour that is running, and the one that has already gone.
        var refused = await PostAsync(booker, venue.Id, (courtId, today, hour));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.StartsTooSoon, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task An_hour_the_venue_is_closed_for_cannot_be_taken()
    {
        var (_, venue, courtId) = await BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();

        var refused = await PostAsync(booker, venue.Id, (courtId, Tomorrow, 3));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.HourNotAvailable, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_court_taken_out_of_use_cannot_be_booked()
    {
        var (owner, venue, courtId) = await BookableVenueAsync();
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PutAsJsonAsync(
                $"/api/venues/{venue.Id}/courts/{courtId}/status",
                new ChangeCourtStatusRequest(false, VenueScenario.Today))).StatusCode);
        var booker = await scenario.SignedInClientAsync();

        var refused = await PostAsync(booker, venue.Id, (courtId, Tomorrow, 18));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.HourNotAvailable, await refused.ErrorCodeAsync());
    }

    [Theory]
    [InlineData(31, AvailabilityErrorCodes.DateTooFarAhead)]
    [InlineData(-1, AvailabilityErrorCodes.DateInThePast)]
    public async Task A_day_outside_the_booking_window_cannot_be_taken(int daysAhead, string expected)
    {
        var (_, venue, courtId) = await BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();

        var refused = await PostAsync(
            booker, venue.Id, (courtId, VenueScenario.Today.AddDays(daysAhead), 18));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(expected, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task The_same_hour_cannot_be_asked_for_twice_in_one_booking()
    {
        var (_, venue, courtId) = await BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();

        var refused = await PostAsync(
            booker, venue.Id, (courtId, Tomorrow, 18), (courtId, Tomorrow, 18));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.DuplicateSlot, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_booking_with_no_hours_is_refused()
    {
        var (_, venue, _) = await BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();

        var refused = await booker.PostAsJsonAsync(
            "/api/bookings", new CreateBookingRequest(venue.Id, []));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.NoSlots, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_venue_a_booker_cannot_see_cannot_be_booked()
    {
        var (_, venue, courtId) = await BookableVenueAsync();
        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);
        var booker = await scenario.SignedInClientAsync();

        var refused = await PostAsync(booker, venue.Id, (courtId, Tomorrow, 18));

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(VenueErrorCodes.NotFound, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Booking_needs_an_account()
    {
        var (_, venue, courtId) = await BookableVenueAsync();

        var refused = await PostAsync(api.CreateClient(), venue.Id, (courtId, Tomorrow, 18));

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    private static DateOnly Tomorrow => VenueScenario.Today.AddDays(1);

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        Guid venueId,
        params (Guid CourtId, DateOnly Date, int Hour)[] slots) =>
        client.PostAsJsonAsync(
            "/api/bookings",
            new CreateBookingRequest(
                venueId,
                [.. slots.Select(slot => new BookingSlotRequest(slot.CourtId, slot.Date, slot.Hour))]));

    /// <summary>An approved venue, open and priced, with one court to book.</summary>
    private async Task<(HttpClient Owner, VenueResponse Venue, Guid CourtId)> BookableVenueAsync()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        return (owner, venue, courts[0]);
    }

    private static async Task<BookingResponse> HoldAsync(
        HttpClient client,
        Guid venueId,
        params (Guid CourtId, DateOnly Date, int Hour)[] slots) =>
        await VenueScenario.ReadAsync<BookingResponse>(
            await PostAsync(client, venueId, slots), HttpStatusCode.Created);

}
