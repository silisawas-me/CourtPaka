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

    // Read each time rather than once for the class: the helpers that make bookings read the
    // date again too, and a run that crosses Bangkok midnight must not ask about the old tomorrow.
    private static DateOnly Today => VenueScenario.Today;
    private static DateOnly Yesterday => Today.AddDays(-1);
    private static DateOnly Tomorrow => Today.AddDays(1);

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

    /// <summary>
    /// Cancelled while the slip was being checked: until the venue says whether the money came,
    /// nobody knows what it kept, so it keeps nothing yet (PRD 6.2).
    /// </summary>
    [Fact]
    public async Task A_cancellation_nobody_has_settled_is_not_revenue()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, waiting) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var cancelled = await booker.PostAsync($"/api/bookings/{waiting.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var figures = await ReadAsync(owner, venue.Id, Tomorrow, Tomorrow);

        Assert.Equal(0m, figures.OnlineBaht);
        Assert.Equal(1, figures.Attention.PaymentsUnanswered);
    }

    /// <summary>
    /// Part of a cancelled booking goes back and the rest stays: kept is the booking less what it
    /// owes, whatever share the terms give (PRD 6.2).
    /// </summary>
    [Fact]
    public async Task A_cancellation_that_gives_part_back_keeps_the_rest()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booking = await ConfirmedOnlineAsync(owner, venue.Id, courts[0], 18);
        await SetRefundAsync(booking, BookingStatus.Cancelled, refundDue: 50m);

        var figures = await ReadAsync(owner, venue.Id, Tomorrow, Tomorrow);

        Assert.Equal(150m, figures.OnlineBaht);
    }

    /// <summary>
    /// Being played right now is neither: not revenue until it is over, not advance once it has
    /// started (PRD US-15).
    /// </summary>
    [Fact]
    public async Task A_booking_being_played_is_neither_revenue_nor_advance()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(
            booker, venue.Id, Tomorrow, (courts[0], 18), (courts[0], 19));
        Assert.Equal(
            HttpStatusCode.OK,
            (await VenueScenario.UploadAsync(booker, booking.Id, VenueScenario.Jpeg())).StatusCode);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-30));

        var day = await ServiceDayAsync(booking.Id);
        var figures = await ReadAsync(owner, venue.Id, day, day);

        Assert.Equal(0m, figures.OnlineBaht);
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
    /// A closure lifted early shut the hours before the lift. The grid rightly forgets it; the
    /// report must not (PRD US-15).
    /// </summary>
    [Fact]
    public async Task A_closure_lifted_early_still_shut_the_hours_before_the_lift()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var closed = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/courts/{courts[1]}/closures",
            new CloseCourtRequest(Tomorrow, 6, Tomorrow.AddDays(1), 22, "ซ่อมพื้น"));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        await LiftAtAsync(courts[1], PlatformRequirements.BangkokHour(Tomorrow, 12));

        var figures = await ReadAsync(owner, venue.Id, Tomorrow, Tomorrow.AddDays(1));

        // Tomorrow 06:00–12:00 was shut on the second court; the day after, nothing was.
        Assert.Equal(32 - 6, figures.Days[0].SellableHours);
        Assert.Equal(32, figures.Days[1].SellableHours);
    }

    /// <summary>Each day is read with the opening hours in force on it (PRD US-11, US-15).</summary>
    [Fact]
    public async Task Opening_hours_changed_part_way_through_are_read_day_by_day()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        await VenueScenario.SetHoursAsync(owner, venue.Id, Tomorrow.AddDays(1), opens: 8, closes: 20);

        var figures = await ReadAsync(owner, venue.Id, Tomorrow, Tomorrow.AddDays(1));

        Assert.Equal(16, figures.Days[0].SellableHours);
        Assert.Equal(12, figures.Days[1].SellableHours);
    }

    [Fact]
    public async Task A_court_taken_out_of_use_part_way_through_sells_nothing_after()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var changed = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/courts/{courts[1]}/status",
            new ChangeCourtStatusRequest(false, Tomorrow.AddDays(1)));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var figures = await ReadAsync(owner, venue.Id, Tomorrow, Tomorrow.AddDays(1));

        Assert.Equal(32, figures.Days[0].SellableHours);
        Assert.Equal(16, figures.Days[1].SellableHours);
    }

    /// <summary>One venue's bookings are nowhere in another's figures (PRD 8).</summary>
    [Fact]
    public async Task Another_venue_s_bookings_are_not_counted_here()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var (otherOwner, other, otherCourts) = await scenario.BookableVenueAsync();
        var elsewhere = await ConfirmedOnlineAsync(otherOwner, other.Id, otherCourts[0], 18);
        await PlayOnAsync(elsewhere, Yesterday, 10);
        await CounterAsync(otherOwner, other.Id, otherCourts[0], 19);

        var mine = await ReadAsync(owner, venue.Id, Yesterday, Tomorrow);
        var theirs = await ReadAsync(otherOwner, other.Id, Yesterday, Tomorrow);

        Assert.Equal((0m, 0m, 0m, 0), (mine.OnlineBaht, mine.StaffBaht, mine.AdvanceBaht, mine.BookedHours));
        Assert.Equal(200m, theirs.OnlineBaht);
        Assert.Equal(200m, theirs.AdvanceBaht);
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

    /// <summary>
    /// Ends a booking with part of it owed back, in the database: which share a set of terms gives
    /// is Cancellation's to test; what the dashboard does with the amount is this class's.
    /// </summary>
    private async Task SetRefundAsync(Guid bookingId, BookingStatus status, decimal refundDue)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await database.Bookings
            .Where(booking => booking.Id == bookingId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(booking => booking.Status, status)
                .SetProperty(booking => booking.RefundDueBaht, refundDue));
    }

    private async Task LiftAtAsync(Guid courtId, DateTimeOffset at)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await database.CourtClosures
            .Where(closure => closure.CourtId == courtId)
            .ExecuteUpdateAsync(set => set.SetProperty(closure => closure.LiftedAt, at));
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
