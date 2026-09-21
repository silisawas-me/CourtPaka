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

/// <summary>The platform's figures: PRD US-22, counted as in 6.2.</summary>
public sealed class PlatformDashboardTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    private static DateOnly Today => VenueScenario.Today;
    private static DateOnly Yesterday => Today.AddDays(-1);
    private static DateOnly Tomorrow => Today.AddDays(1);

    [Fact]
    public async Task Each_venue_s_bookings_are_counted_by_channel_on_the_day_played()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 18);
        await CounterAsync(owner, venue.Id, courts[0], 19);
        await scenario.WaitingBookingAsync(venue.Id, courts[0], 20);

        var line = Assert.Single(
            (await ReadAsync(admin, Tomorrow, Tomorrow)).Venues, one => one.VenueId == venue.Id);

        // The slip still waiting is not sold yet (PRD US-22 counts Confirmed/Completed/NoShow).
        Assert.Equal((2, 1, 1), (line.Bookings, line.OnlineBookings, line.StaffBookings));
    }

    /// <summary>GMV is what venues kept from online bookings that have ended (PRD US-22, 6.2).</summary>
    [Fact]
    public async Task GMV_is_online_money_kept_from_bookings_that_have_ended()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        var played = await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 18);
        await PlayOnAsync(played, Yesterday, 10);
        var counter = await CounterAsync(owner, venue.Id, courts[0], 19);
        await PlayOnAsync(counter, Yesterday, 11);
        await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 20);

        var figures = await ReadAsync(admin, Yesterday, Tomorrow);
        var line = Assert.Single(figures.Venues, one => one.VenueId == venue.Id);

        // Only the online booking already played: the counter's sale is the venue's own, and
        // tomorrow's has not ended.
        Assert.Equal(200m, line.GmvBaht);
        Assert.True(figures.Totals.GmvBaht >= 200m);
    }

    /// <summary>
    /// Every ending of 6.2 in one place: a cancellation keeps what it does not owe back, one
    /// nobody has settled keeps nothing yet, and a no-show keeps all of it (PRD 6.2).
    /// </summary>
    [Fact]
    public async Task GMV_follows_every_ending_that_keeps_money()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        var cancelled = await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 10);
        await EndAsync(cancelled, BookingStatus.Cancelled, PaymentState.Received, refundDue: 50m);
        var unsettled = await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 11);
        await EndAsync(unsettled, BookingStatus.Cancelled, PaymentState.Unconfirmed, refundDue: 0m);
        var noShow = await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 12);
        await EndAsync(noShow, BookingStatus.NoShow, PaymentState.Received, refundDue: 0m);

        var line = Assert.Single(
            (await ReadAsync(admin, Tomorrow, Tomorrow)).Venues, one => one.VenueId == venue.Id);

        Assert.Equal(150m + 0m + 200m, line.GmvBaht);
        // Sold is Confirmed/Completed/NoShow; the cancelled two are not.
        Assert.Equal(1, line.Bookings);
    }

    [Fact]
    public async Task A_day_outside_the_range_is_not_counted()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 18);

        var line = Assert.Single(
            (await ReadAsync(admin, Yesterday, Yesterday)).Venues, one => one.VenueId == venue.Id);

        Assert.Equal(0, line.Bookings);
    }

    [Fact]
    public async Task Venues_are_counted_by_where_they_stand_now()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (_, venue, _) = await scenario.BookableVenueAsync();
        var before = await ReadAsync(admin, Today, Today);

        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);
        var after = await ReadAsync(admin, Today, Today);

        Assert.Equal(before.VenuesByStatus["Suspended"] + 1, after.VenuesByStatus["Suspended"]);
        Assert.Equal(before.VenuesByStatus["Approved"] - 1, after.VenuesByStatus["Approved"]);
    }

    [Fact]
    public async Task Only_the_platform_may_read_it()
    {
        var (owner, _, _) = await scenario.BookableVenueAsync();

        var refused = await owner.GetAsync("/api/admin/dashboard");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task A_range_longer_than_a_year_is_refused()
    {
        var admin = await scenario.PlatformAdminAsync();

        var refused = await admin.GetAsync(
            $"/api/admin/dashboard?from={Today:yyyy-MM-dd}&to={Today.AddDays(366):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(DashboardErrorCodes.InvalidRange, await refused.ErrorCodeAsync());
    }

    // ---------------------------------------------------------------------------------------

    private async Task<Guid> ConfirmedOnlineAsync(HttpClient owner, Guid venueId, Guid courtId, int hour)
    {
        var (_, booking) = await scenario.WaitingBookingAsync(venueId, courtId, hour);
        var confirmed = await owner.PostAsync(
            $"/api/venues/{venueId}/slip-queue/{booking.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        return booking.Id;
    }

    private static async Task<Guid> CounterAsync(HttpClient owner, Guid venueId, Guid courtId, int hour)
    {
        var taken = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/bookings",
                new CounterBookingRequest(
                    [new BookingSlotRequest(courtId, Tomorrow, hour)], "คุณสมชาย", null, "Cash")),
            HttpStatusCode.Created);
        return taken.BookingId;
    }

    private async Task EndAsync(
        Guid bookingId,
        BookingStatus status,
        PaymentState payment,
        decimal refundDue)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await database.Bookings
            .Where(booking => booking.Id == bookingId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(booking => booking.Status, status)
                .SetProperty(booking => booking.PaymentState, payment)
                .SetProperty(booking => booking.RefundDueBaht, refundDue));
    }

    private async Task PlayOnAsync(Guid bookingId, DateOnly date, int hour)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var starts = PlatformRequirements.BangkokHour(date, hour);

        await database.BookingSlots
            .Where(slot => slot.BookingId == bookingId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(slot => slot.StartsAt, starts)
                .SetProperty(slot => slot.EndsAt, starts.AddHours(1)));
    }

    private static async Task<PlatformDashboardResponse> ReadAsync(
        HttpClient admin,
        DateOnly from,
        DateOnly to) =>
        await VenueScenario.ReadAsync<PlatformDashboardResponse>(
            await admin.GetAsync($"/api/admin/dashboard?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}"));
}
