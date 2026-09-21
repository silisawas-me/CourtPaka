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

/// <summary>A venue's own figures: PRD US-15, with the definitions of 6.2.</summary>
public sealed class VenueDashboardTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    private static readonly DateOnly Today = VenueScenario.Today;
    private static readonly DateOnly Yesterday = Today.AddDays(-1);
    private static readonly DateOnly Tomorrow = Today.AddDays(1);

    [Fact]
    public async Task Revenue_is_what_was_kept_on_the_day_it_was_played_by_channel()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        var online = await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 18);
        await PlayOnAsync(online, Yesterday, 10);

        var counter = await CounterAsync(owner, venue.Id, courts[0], 19);
        await PlayOnAsync(counter, Yesterday, 11);

        var figures = await ReadAsync(owner, venue.Id, Yesterday, Yesterday);

        Assert.Equal(200m, figures.OnlineBaht);
        Assert.Equal(200m, figures.StaffBaht);
        var day = Assert.Single(figures.Days);
        Assert.Equal((200m, 200m), (day.OnlineBaht, day.StaffBaht));
        var month = Assert.Single(figures.Months);
        Assert.Equal((Yesterday.Year, Yesterday.Month), (month.Year, month.Month));
    }

    /// <summary>Confirmed and still ahead is money coming, not money made (PRD US-15).</summary>
    [Fact]
    public async Task A_booking_not_yet_played_is_advance_money_not_revenue()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 18);

        var figures = await ReadAsync(owner, venue.Id, Tomorrow, Tomorrow);

        Assert.Equal(0m, figures.OnlineBaht);
        Assert.Equal(200m, figures.AdvanceBaht);
    }

    /// <summary>The advance does not depend on the range: it is what is coming, as of now.</summary>
    [Fact]
    public async Task The_advance_ignores_the_range()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 18);

        var figures = await ReadAsync(owner, venue.Id, Yesterday, Yesterday);

        Assert.Equal(200m, figures.AdvanceBaht);
    }

    [Fact]
    public async Task Money_that_never_arrived_is_not_revenue()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, waiting) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await PlayOnAsync(waiting.Id, Yesterday, 10);

        var figures = await ReadAsync(owner, venue.Id, Yesterday, Yesterday);

        Assert.Equal(0m, figures.OnlineBaht);
        Assert.Equal(1, figures.Attention.SlipsToCheck);
    }

    /// <summary>
    /// Kept is the booking less what it owes back, not less what has been sent back — so the
    /// number does not move when the refund is written down (PRD 6.2).
    /// </summary>
    [Fact]
    public async Task A_venue_that_cancelled_keeps_nothing_and_owes_it_all_back()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booking = await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 18);

        var cancelled = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking}/cancel",
            new VenueCancelRequest(nameof(CancellationReason.VenueInitiated), null, null));
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var figures = await ReadAsync(owner, venue.Id, Tomorrow, Tomorrow);

        Assert.Equal(0m, figures.OnlineBaht);
        Assert.Equal(1, figures.Attention.RefundsOutstanding);
        Assert.Equal(0m, figures.AdvanceBaht);
    }

    [Fact]
    public async Task A_no_show_keeps_what_was_paid()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booking = await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 18);
        await scenario.StartsInAsync(booking, TimeSpan.FromMinutes(-20));

        var marked = await owner.PostAsync(
            $"/api/venues/{venue.Id}/bookings/{booking}/no-show", null);
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);

        var day = await ServiceDayAsync(booking);
        var figures = await ReadAsync(owner, venue.Id, day, day);

        Assert.Equal(200m, figures.OnlineBaht);
    }

    [Fact]
    public async Task Utilisation_is_hours_used_over_hours_the_venue_had_to_sell()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 18);

        var figures = await ReadAsync(owner, venue.Id, Tomorrow, Tomorrow);

        // Open 06:00–22:00 on two courts.
        Assert.Equal(32, figures.SellableHours);
        Assert.Equal(1, figures.BookedHours);
        Assert.Equal(3.1m, figures.UtilizationPercent);
    }

    /// <summary>A court shut for a while had nothing to sell for that while (PRD US-11, US-15).</summary>
    [Fact]
    public async Task Hours_a_court_was_shut_are_not_hours_it_had_to_sell()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var closed = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/courts/{courts[1]}/closures",
            new CloseCourtRequest(Tomorrow, 6, Tomorrow, 10, "ซ่อมพื้น"));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var figures = await ReadAsync(owner, venue.Id, Tomorrow, Tomorrow);

        Assert.Equal(28, figures.SellableHours);
    }

    /// <summary>
    /// A no-show whose hour then went to a walk-in used that hour once, not twice, so the share
    /// can never pass 100% (PRD US-15).
    /// </summary>
    [Fact]
    public async Task An_hour_sold_twice_is_one_hour_used()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booking = await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 18);
        await NoShowInTheDatabaseAsync(booking);
        await scenario.SomebodyElseTakesAsync(booking);

        var figures = await ReadAsync(owner, venue.Id, Tomorrow, Tomorrow);

        Assert.Equal(1, figures.BookedHours);
    }

    [Fact]
    public async Task A_venue_with_nothing_to_sell_has_no_utilisation_rather_than_none()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync(courts: 0);

        var figures = await ReadAsync(owner, venue.Id, Tomorrow, Tomorrow);

        Assert.Equal(0, figures.SellableHours);
        Assert.Null(figures.UtilizationPercent);
    }

    [Fact]
    public async Task With_no_range_it_is_this_month()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        var figures = await VenueScenario.ReadAsync<DashboardResponse>(
            await owner.GetAsync($"/api/venues/{venue.Id}/dashboard"));

        Assert.Equal(new DateOnly(Today.Year, Today.Month, 1), figures.From);
        Assert.Equal(DateTime.DaysInMonth(Today.Year, Today.Month), figures.Days.Length);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 366)]
    public async Task A_range_backwards_or_longer_than_a_year_is_refused(int fromOffset, int toOffset)
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        var refused = await owner.GetAsync(
            $"/api/venues/{venue.Id}/dashboard?from={Today.AddDays(fromOffset):yyyy-MM-dd}"
            + $"&to={Today.AddDays(toOffset):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(DashboardErrorCodes.InvalidRange, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Staff_need_ViewReports()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var without = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageBookings));
        var with = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ViewReports));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await without.GetAsync($"/api/venues/{venue.Id}/dashboard")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await with.GetAsync($"/api/venues/{venue.Id}/dashboard")).StatusCode);
    }

    /// <summary>One venue's figures are nobody else's (PRD 8).</summary>
    [Fact]
    public async Task Another_venue_s_owner_cannot_read_them()
    {
        var (_, venue, _) = await scenario.BookableVenueAsync();
        var (stranger, _, _) = await scenario.BookableVenueAsync();

        var refused = await stranger.GetAsync($"/api/venues/{venue.Id}/dashboard");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>A suspension stops selling, not reading what was sold (PRD US-20).</summary>
    [Fact]
    public async Task A_suspended_venue_can_still_read_its_figures()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);

        var read = await owner.GetAsync($"/api/venues/{venue.Id}/dashboard");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
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

    /// <summary>Moves a one-hour booking to a Bangkok date and hour, in the database.</summary>
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

    private async Task NoShowInTheDatabaseAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await database.Bookings
            .Where(booking => booking.Id == bookingId)
            .ExecuteUpdateAsync(set => set.SetProperty(booking => booking.Status, BookingStatus.NoShow));
        await database.BookingSlots
            .Where(slot => slot.BookingId == bookingId)
            .ExecuteUpdateAsync(set => set.SetProperty(slot => slot.IsActive, false));
    }

    private async Task<DateOnly> ServiceDayAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var starts = await database.BookingSlots
            .Where(slot => slot.BookingId == bookingId)
            .MinAsync(slot => slot.StartsAt);
        return PlatformRequirements.BangkokDateAndHour(starts).Date;
    }

    private static async Task<DashboardResponse> ReadAsync(
        HttpClient client,
        Guid venueId,
        DateOnly from,
        DateOnly to) =>
        await VenueScenario.ReadAsync<DashboardResponse>(
            await client.GetAsync(
                $"/api/venues/{venueId}/dashboard?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}"));
}
