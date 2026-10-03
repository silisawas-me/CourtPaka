using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>
/// The queue for a day a venue has already sold (PRD US-27): what it takes, who may read it, and
/// what standing down does.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WaitlistTests(ApiTestFixture api)
{
    private readonly VenueScenario scenario = new(api);

    private static readonly DateOnly Tomorrow = VenueScenario.Today.AddDays(1);

    [Fact]
    public async Task Somebody_who_found_nothing_can_wait_for_it()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();

        var joined = await VenueScenario.ReadAsync<WaitlistEntryResponse>(
            await booker.PostAsJsonAsync(
                "/api/waitlist", new JoinWaitlistRequest(venue.Id, Tomorrow, 18, 22, 2)),
            HttpStatusCode.Created);

        Assert.Equal(nameof(WaitlistState.Waiting), joined.State);
        Assert.Equal(venue.Name, joined.VenueName);

        // It is theirs to read back, and the venue's to ring.
        var mine = await VenueScenario.ReadAsync<WaitlistEntryResponse[]>(
            await booker.GetAsync("/api/waitlist"));
        Assert.Contains(mine, one => one.Id == joined.Id);

        var queue = await VenueScenario.ReadAsync<VenueWaitlistEntryResponse[]>(
            await owner.GetAsync($"/api/venues/{venue.Id}/waitlist?date={Tomorrow:yyyy-MM-dd}"));
        var waiting = Assert.Single(queue, one => one.Id == joined.Id);
        Assert.Equal(2, waiting.Hours);
        Assert.NotNull(waiting.BookerEmail);
    }

    /// <summary>
    /// A window has to be a window and the run has to fit inside it, or the queue would be full
    /// of wants nothing could ever answer (PRD US-27).
    /// </summary>
    [Theory]
    [InlineData(22, 18, 2, WaitlistErrorCodes.WindowNotAWindow)]
    [InlineData(18, 19, 2, WaitlistErrorCodes.HoursDoNotFit)]
    [InlineData(18, 22, 0, WaitlistErrorCodes.HoursDoNotFit)]
    [InlineData(6, 24, 5, WaitlistErrorCodes.HoursDoNotFit)]
    public async Task A_want_nothing_could_answer_is_refused(
        int fromHour,
        int untilHour,
        int hours,
        string code)
    {
        var (_, venue, _) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();

        var refused = await booker.PostAsJsonAsync(
            "/api/waitlist", new JoinWaitlistRequest(venue.Id, Tomorrow, fromHour, untilHour, hours));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(code, await refused.ErrorCodeAsync());
    }

    /// <summary>A day nobody can book is a day nobody can wait for either (PRD S-04).</summary>
    [Fact]
    public async Task A_day_the_venue_does_not_sell_cannot_be_waited_for()
    {
        var (_, venue, _) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();

        foreach (var day in new[]
        {
            VenueScenario.Today.AddDays(-1),
            VenueScenario.Today.AddDays(Availability.BookableDaysAhead + 1),
        })
        {
            var refused = await booker.PostAsJsonAsync(
                "/api/waitlist", new JoinWaitlistRequest(venue.Id, day, 18, 22, 2));

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(WaitlistErrorCodes.DayNotOpen, await refused.ErrorCodeAsync());
        }
    }

    /// <summary>
    /// One place per day per venue. Somebody who wants two windows on one day wants the wider
    /// one, and a queue holding the same name twice would offer the same hour twice.
    /// </summary>
    [Fact]
    public async Task Nobody_stands_in_the_same_queue_twice()
    {
        var (_, venue, _) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();

        Assert.Equal(
            HttpStatusCode.Created,
            (await booker.PostAsJsonAsync(
                "/api/waitlist",
                new JoinWaitlistRequest(venue.Id, Tomorrow, 18, 22, 2))).StatusCode);

        var again = await booker.PostAsJsonAsync(
            "/api/waitlist", new JoinWaitlistRequest(venue.Id, Tomorrow, 6, 10, 1));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(WaitlistErrorCodes.AlreadyWaiting, await again.ErrorCodeAsync());

        // And the next day is a different queue, so it is allowed.
        Assert.Equal(
            HttpStatusCode.Created,
            (await booker.PostAsJsonAsync(
                "/api/waitlist",
                new JoinWaitlistRequest(venue.Id, Tomorrow.AddDays(1), 18, 22, 2))).StatusCode);
    }

    /// <summary>
    /// Standing down empties the place but keeps the row: how long a queue was and how it ended
    /// is the only way to answer whether it was worth having (PRD US-27).
    /// </summary>
    [Fact]
    public async Task Standing_down_gives_the_place_up_and_can_be_done_once()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var joined = await VenueScenario.ReadAsync<WaitlistEntryResponse>(
            await booker.PostAsJsonAsync(
                "/api/waitlist", new JoinWaitlistRequest(venue.Id, Tomorrow, 18, 22, 2)),
            HttpStatusCode.Created);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await booker.DeleteAsync($"/api/waitlist/{joined.Id}")).StatusCode);

        Assert.Empty(
            await VenueScenario.ReadAsync<WaitlistEntryResponse[]>(
                await booker.GetAsync("/api/waitlist")));

        var queue = await VenueScenario.ReadAsync<VenueWaitlistEntryResponse[]>(
            await owner.GetAsync($"/api/venues/{venue.Id}/waitlist"));
        Assert.DoesNotContain(queue, one => one.Id == joined.Id);

        // Twice is not a second ending.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await booker.DeleteAsync($"/api/waitlist/{joined.Id}")).StatusCode);
    }

    /// <summary>Somebody else's place in a queue answers the same as one that is not there.</summary>
    [Fact]
    public async Task A_place_in_a_queue_belongs_to_whoever_took_it()
    {
        var (_, venue, _) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var stranger = await scenario.SignedInClientAsync();

        var joined = await VenueScenario.ReadAsync<WaitlistEntryResponse>(
            await booker.PostAsJsonAsync(
                "/api/waitlist", new JoinWaitlistRequest(venue.Id, Tomorrow, 18, 22, 2)),
            HttpStatusCode.Created);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await stranger.DeleteAsync($"/api/waitlist/{joined.Id}")).StatusCode);
        Assert.Empty(
            await VenueScenario.ReadAsync<WaitlistEntryResponse[]>(
                await stranger.GetAsync("/api/waitlist")));
    }

    /// <summary>
    /// The queue carries the addresses of people waiting, so it is behind the same permission the
    /// day's list is (PDPA, US-13).
    /// </summary>
    [Fact]
    public async Task Reading_the_queue_needs_the_permission_the_day_list_needs()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.VerifySlip));

        await booker.PostAsJsonAsync(
            "/api/waitlist", new JoinWaitlistRequest(venue.Id, Tomorrow, 18, 22, 2));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await staff.GetAsync($"/api/venues/{venue.Id}/waitlist")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await booker.GetAsync($"/api/venues/{venue.Id}/waitlist")).StatusCode);
    }

    /// <summary>
    /// Waiting is a promise that somebody will be rung, so an account that cannot be rung is
    /// refused at the door rather than found out when an hour comes free (PRD S-15).
    /// </summary>
    [Fact]
    public async Task An_account_nobody_could_ring_cannot_wait()
    {
        var (_, venue, _) = await scenario.BookableVenueAsync();
        var unverified = await scenario.SignedInClientAsync(verifyEmail: false);

        var refused = await unverified.PostAsJsonAsync(
            "/api/waitlist", new JoinWaitlistRequest(venue.Id, Tomorrow, 18, 22, 2));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
}
