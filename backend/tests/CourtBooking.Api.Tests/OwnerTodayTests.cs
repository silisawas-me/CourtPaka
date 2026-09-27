using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>
/// Today at every venue a person reads the reports of, on one page (badPaka 2c). Each venue is
/// asked what its own dashboard would say, behind the same door the dashboard has.
/// </summary>
public sealed class OwnerTodayTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    private static async Task<OwnerTodayResponse> TodayAsync(HttpClient client) =>
        await VenueScenario.ReadAsync<OwnerTodayResponse>(
            await client.GetAsync("/api/venues/mine/today"), HttpStatusCode.OK);

    [Fact]
    public async Task An_owner_sees_every_venue_they_own()
    {
        var (owner, first, _) = await scenario.BookableVenueAsync();
        var second = await scenario.CreateVenueAsync(owner);
        await scenario.SetStatusAsync(second.Id, VenueStatus.Approved);

        var today = await TodayAsync(owner);

        Assert.Equal(VenueScenario.Today, today.Date);
        Assert.Equal(
            new[] { first.Id, second.Id }.Order(),
            today.Venues.Select(venue => venue.VenueId).Order());
    }

    /// <summary>
    /// The same door as the dashboard (PRD US-14): staff who may take bookings but not read the
    /// reports do not see the venue's money here either.
    /// </summary>
    [Fact]
    public async Task Staff_see_a_venue_only_where_they_may_read_its_reports()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var desk = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageBookings));
        var manager = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ViewReports));

        Assert.Empty((await TodayAsync(desk)).Venues);
        Assert.Equal(venue.Id, Assert.Single((await TodayAsync(manager)).Venues).VenueId);
    }

    [Fact]
    public async Task A_venue_still_applying_is_read_with_nothing_in_it()
    {
        var owner = await scenario.SignedInClientAsync();
        await scenario.CreateVenueAsync(owner);

        // Pending is not frozen, so it is read like the dashboard would be — with nothing in it.
        var venue = Assert.Single((await TodayAsync(owner)).Venues);
        Assert.Equal(nameof(VenueStatus.Pending), venue.Status);
        Assert.Equal(0, venue.Bookings);
    }

    [Fact]
    public async Task A_suspended_venue_is_still_read_as_its_dashboard_is()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);

        Assert.Equal(venue.Id, Assert.Single((await TodayAsync(owner)).Venues).VenueId);
    }

    /// <summary>
    /// The numbers are the dashboard's, from the same code: the owner's overview and the venue's
    /// own page must never show two different amounts for the same day.
    /// </summary>
    [Fact]
    public async Task Today_says_what_the_venue_dashboard_says_about_today()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-20));

        var overview = Assert.Single((await TodayAsync(owner)).Venues);
        var today = VenueScenario.Today.ToString("yyyy-MM-dd");
        var dashboard = await VenueScenario.ReadAsync<DashboardResponse>(
            await owner.GetAsync($"/api/venues/{venue.Id}/dashboard?from={today}&to={today}"),
            HttpStatusCode.OK);

        Assert.Equal(dashboard.OnlineBaht + dashboard.StaffBaht, overview.KeptBaht);
        Assert.Equal(dashboard.SellableHours, overview.SellableHours);
        Assert.Equal(dashboard.BookedHours, overview.BookedHours);
        Assert.Equal(dashboard.UtilizationPercent, overview.UsedPercent);
        // And the hours add up to the day, which is how the day is counted.
        Assert.Equal(overview.SellableHours, overview.Hours.Sum(hour => hour.Sellable));
        Assert.Equal(overview.BookedHours, overview.Hours.Sum(hour => hour.Booked));
    }

    /// <summary>
    /// Waiting to be taken in is the check-in door's own rule (US-24), and past the venue's grace
    /// is where the no-show door opens — the two things an owner looking at every venue at once
    /// wants flagged.
    /// </summary>
    [Fact]
    public async Task Somebody_on_court_now_and_not_taken_in_is_due_and_late()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-20));

        var overview = await TodayAsync(owner);
        var only = Assert.Single(overview.Venues);

        Assert.Equal(1, only.DueNow);
        // Twenty minutes in, against the venue's fifteen.
        Assert.Equal(1, only.PastGrace);
        Assert.Equal(1, overview.DueNow);

        // Taken in: no longer waiting.
        (await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/check-in", null))
            .EnsureSuccessStatusCode();
        Assert.Equal(0, Assert.Single((await TodayAsync(owner)).Venues).DueNow);
    }

    [Fact]
    public async Task A_court_shut_this_hour_is_named()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);

        using (var scope = api.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ownerId = await database.VenueMemberships
                .Where(member => member.VenueId == venue.Id)
                .Select(member => member.UserId)
                .SingleAsync();
            // Written straight in: the door that closes courts refuses to start in the past,
            // and "now" is always a little in the past by the time it is written.
            var now = DateTimeOffset.UtcNow;
            database.CourtClosures.Add(new CourtClosure
            {
                CourtId = courts[1],
                StartsAt = now.AddMinutes(-30),
                EndsAt = now.AddHours(2),
                Reason = "ซ่อมพื้น",
                CreatedByUserId = ownerId,
                CreatedAt = now,
            });
            await database.SaveChangesAsync();
        }

        var only = Assert.Single((await TodayAsync(owner)).Venues);
        Assert.Equal(["Court 2"], only.ShutNow);

        // Lifted early: open again, and no longer named.
        using (var scope = api.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await database.CourtClosures
                .Where(closure => closure.CourtId == courts[1])
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(closure => closure.LiftedAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        Assert.Empty(Assert.Single((await TodayAsync(owner)).Venues).ShutNow);
    }
}
