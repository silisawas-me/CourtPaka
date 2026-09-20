using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>The counter managing a booking somebody else made: PRD US-13, 6.1, 6.2.</summary>
public sealed class VenueBookingTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task The_day_shows_what_was_taken_and_which_doors_are_open()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var day = await DayAsync(owner, venue.Id, VenueScenario.Today.AddDays(1));

        var waiting = Assert.Single(day);
        Assert.Equal(booking.Id, waiting.BookingId);
        Assert.Equal(nameof(BookingStatus.PendingVerification), waiting.Status);
        Assert.Equal(booking.TotalBaht, waiting.TotalBaht);
        Assert.NotNull(waiting.BookerEmail);

        // It can be turned away, but nobody is late for tomorrow and there is nothing to settle.
        Assert.True(waiting.Can.Cancel);
        Assert.False(waiting.Can.NoShow);
        Assert.False(waiting.Can.SettlePayment);
        Assert.False(waiting.Can.PlayedAfterAll);
    }

    [Fact]
    public async Task A_day_with_nothing_on_it_is_empty_rather_than_missing()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        Assert.Empty(await DayAsync(owner, venue.Id, VenueScenario.Today.AddDays(2)));
    }

    [Fact]
    public async Task Another_venue_cannot_read_this_one_s_day()
    {
        var (_, venue, _) = await scenario.BookableVenueAsync();
        var (stranger, _, _) = await scenario.BookableVenueAsync();

        var refused = await stranger.GetAsync(
            $"/api/venues/{venue.Id}/bookings?date={VenueScenario.Today.AddDays(1):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Staff_without_the_permission_may_look_but_not_touch()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.VerifySlip));

        Assert.Single(await DayAsync(staff, venue.Id, VenueScenario.Today.AddDays(1)));
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await CancelAsync(staff, venue.Id, booking.Id, paymentReceived: false)).StatusCode);
    }

    [Fact]
    public async Task Letting_a_hold_go_at_the_counter_needs_no_reason()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[0], 18));

        var cancelled = await CancelledAsync(owner, venue.Id, booking.Id);

        Assert.Equal(nameof(BookingStatus.Cancelled), cancelled.Status);
        Assert.Equal(0m, cancelled.RefundDueBaht);
        Assert.False(await scenario.HoldsItsHoursAsync(booking.Id));
    }

    [Fact]
    public async Task Turning_away_a_slip_being_checked_asks_whether_the_money_arrived()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/cancel",
            new VenueCancelRequest(null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.PaymentAnswerRequired, await refused.ErrorCodeAsync());
    }

    [Theory]
    [InlineData(true, nameof(PaymentState.Received))]
    [InlineData(false, nameof(PaymentState.NotReceived))]
    public async Task What_the_venue_says_about_the_money_decides_what_goes_back(
        bool paymentReceived,
        string expectedPayment)
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var cancelled = await CancelledAsync(
            owner, venue.Id, booking.Id, paymentReceived: paymentReceived);

        Assert.Equal(nameof(BookingStatus.Cancelled), cancelled.Status);
        Assert.Equal(expectedPayment, cancelled.PaymentState);
        Assert.Equal(paymentReceived ? booking.TotalBaht : 0m, cancelled.RefundDueBaht);
    }

    [Fact]
    public async Task Turning_away_a_confirmed_booking_asks_why()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/cancel",
            new VenueCancelRequest(null, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.ReasonRequired, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_venue_that_cannot_honour_a_booking_keeps_none_of_the_money()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();

        var cancelled = await CancelledAsync(
            owner, venue.Id, booking.Id, reason: nameof(CancellationReason.VenueInitiated));

        Assert.Equal(booking.TotalBaht, cancelled.RefundDueBaht);
        Assert.Equal(nameof(PaymentState.Received), cancelled.PaymentState);
        Assert.False(await scenario.HoldsItsHoursAsync(booking.Id));
    }

    [Fact]
    public async Task A_customer_asking_gets_what_the_terms_they_booked_under_give()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();

        // Inside the day's notice the venue's default terms ask for (PRD S-11).
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromHours(2));

        var cancelled = await CancelledAsync(
            owner, venue.Id, booking.Id, reason: nameof(CancellationReason.CustomerRequest));

        Assert.Equal(0m, cancelled.RefundDueBaht);
        Assert.Equal(nameof(PaymentState.Received), cancelled.PaymentState);
    }

    [Fact]
    public async Task A_slip_that_turned_out_to_be_nothing_leaves_no_money_and_no_debt()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();

        var cancelled = await CancelledAsync(
            owner, venue.Id, booking.Id, reason: nameof(CancellationReason.PaymentNotReceived));

        Assert.Equal(nameof(PaymentState.NotReceived), cancelled.PaymentState);
        Assert.Equal(0m, cancelled.RefundDueBaht);
    }

    [Fact]
    public async Task A_reason_the_state_machine_does_not_know_is_refused()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/cancel",
            new VenueCancelRequest("BecauseISaidSo", null, null));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.ReasonNotAllowedHere, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Nobody_is_a_no_show_a_minute_after_the_hour_begins()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-5));

        var refused = await owner.PostAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/no-show", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.NotYetLateEnough, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Nobody_turning_up_owes_nothing_and_gives_the_rest_of_the_hours_back()
    {
        var (owner, venue, booking) = await ConfirmedTwoHoursAsync();

        // Twenty minutes into the first of its two hours: the second has not begun.
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-20));

        var written = await ReadAsync(await owner.PostAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/no-show", null));

        Assert.Equal(nameof(BookingStatus.NoShow), written.Status);
        Assert.Equal(0m, written.RefundDueBaht);
        // The venue keeps what it was paid, and what was not reached goes back on sale — the
        // hour already running does not (PRD 6.1).
        Assert.Equal(nameof(PaymentState.Received), written.PaymentState);
        Assert.Equal(1, await scenario.HoursStillHeldAsync(booking.Id));
    }

    [Fact]
    public async Task What_was_played_may_be_corrected_for_a_day_and_then_not()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();
        await scenario.StartsInAsync(booking.Id, -VenueDecisions.CorrectionWindow);

        // The hours ended an hour inside the window, so the owner may still say what happened.
        var written = await ReadAsync(await owner.PostAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/no-show", null));
        Assert.Equal(nameof(BookingStatus.NoShow), written.Status);

        // The history reads as a chain: the clock completed it, then the owner wrote it off.
        var history = await scenario.HistoryAsync(booking.Id);
        Assert.Equal(
            BookingStatus.Confirmed,
            Assert.Single(history, change => change.To == BookingStatus.Completed).From);
        Assert.Equal(
            BookingStatus.Completed,
            Assert.Single(history, change => change.To == BookingStatus.NoShow).From);
    }

    [Fact]
    public async Task Once_the_day_is_over_what_was_recorded_stands()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();
        await scenario.StartsInAsync(booking.Id, -VenueDecisions.CorrectionWindow.Add(TimeSpan.FromHours(2)));

        var refused = await owner.PostAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/no-show", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.TooLateToCorrect, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Correcting_hours_already_played_is_the_owner_s_alone()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();
        await scenario.StartsInAsync(booking.Id, -VenueDecisions.CorrectionWindow);
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageBookings));

        var refused = await CancelAsync(
            staff, venue.Id, booking.Id, reason: nameof(CancellationReason.VenueInitiated));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.OwnerOnly, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Hours_that_were_played_cannot_be_given_back_at_the_customer_s_request()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();
        await scenario.StartsInAsync(booking.Id, -VenueDecisions.CorrectionWindow);

        var refused = await CancelAsync(
            owner, venue.Id, booking.Id, reason: nameof(CancellationReason.CustomerRequest));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.ReasonNotAllowedHere, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_no_show_written_by_mistake_can_be_taken_back_by_the_owner()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-20));
        await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/no-show", null);

        var corrected = await ReadAsync(await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/played",
            new PlayedAfterAllRequest("มาเล่นจริง พนักงานกดผิด")));

        Assert.Equal(nameof(BookingStatus.Completed), corrected.Status);
        Assert.True(await scenario.HoldsItsHoursAsync(booking.Id));

        var change = Assert.Single(
            await scenario.HistoryAsync(booking.Id),
            recorded => recorded.From == BookingStatus.NoShow);
        Assert.Equal("มาเล่นจริง พนักงานกดผิด", change.Reason);
    }

    [Fact]
    public async Task Taking_a_no_show_back_needs_a_reason_and_needs_the_owner()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-20));
        await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/no-show", null);

        var noReason = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/played",
            new PlayedAfterAllRequest("   "));
        Assert.Equal(BookingErrorCodes.ReasonRequired, await noReason.ErrorCodeAsync());

        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageBookings));
        var notOwner = await staff.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/played",
            new PlayedAfterAllRequest("มาเล่นจริง"));
        Assert.Equal(HttpStatusCode.Forbidden, notOwner.StatusCode);
    }

    [Fact]
    public async Task Hours_sold_to_somebody_else_cannot_be_taken_back()
    {
        var (owner, venue, booking) = await ConfirmedTwoHoursAsync();

        // Written off twenty minutes in, which puts the second hour back on sale, and it is taken.
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-20));
        var written = await owner.PostAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/no-show", null);
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        await scenario.SomebodyElseTakesAsync(booking.Id);

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/played",
            new PlayedAfterAllRequest("มาเล่นจริง"));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.HoursAlreadyTaken, await refused.ErrorCodeAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_venue_settles_a_booking_the_booker_gave_up_mid_check(bool paymentReceived)
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null);

        var settled = await ReadAsync(await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/settle-payment",
            new SettlePaymentRequest(paymentReceived)));

        Assert.Equal(
            paymentReceived ? nameof(PaymentState.Received) : nameof(PaymentState.NotReceived),
            settled.PaymentState);

        // The share was written down when the booker let go; this is what turns it into money.
        Assert.Equal(paymentReceived ? booking.TotalBaht : 0m, settled.RefundDueBaht);
        Assert.False(settled.Can.SettlePayment);
    }

    [Fact]
    public async Task There_is_nothing_to_settle_on_a_booking_nobody_left_unanswered()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/settle-payment",
            new SettlePaymentRequest(true));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.NothingToSettle, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_booking_at_another_venue_is_not_found_here()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var (_, elsewhere, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(elsewhere.Id, courts[0], 18);

        var refused = await CancelAsync(owner, venue.Id, booking.Id, paymentReceived: false);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.NotFound, await refused.ErrorCodeAsync());
    }

    /// <summary>A confirmed booking of two hours running, so there is a "rest" to give back.</summary>
    private async Task<(HttpClient Owner, VenueResponse Venue, BookingResponse Booking)>
        ConfirmedTwoHoursAsync()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(
            booker,
            venue.Id,
            VenueScenario.Today.AddDays(1),
            (courts[0], 18),
            (courts[0], 19));

        await VenueScenario.UploadAsync(booker, booking.Id, VenueScenario.Jpeg());
        var confirmed = await owner.PostAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        return (owner, venue, booking);
    }

    private async Task<(HttpClient Owner, VenueResponse Venue, BookingResponse Booking)>
        ConfirmedBookingAsync()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var confirmed = await owner.PostAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        return (owner, venue, booking);
    }

    private static async Task<VenueBookingResponse[]> DayAsync(
        HttpClient client,
        Guid venueId,
        DateOnly date) =>
        await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await client.GetAsync($"/api/venues/{venueId}/bookings?date={date:yyyy-MM-dd}"));

    private static Task<HttpResponseMessage> CancelAsync(
        HttpClient client,
        Guid venueId,
        Guid bookingId,
        string? reason = null,
        bool? paymentReceived = null) =>
        client.PostAsJsonAsync(
            $"/api/venues/{venueId}/bookings/{bookingId}/cancel",
            new VenueCancelRequest(reason, paymentReceived, null));

    private static async Task<VenueBookingResponse> CancelledAsync(
        HttpClient client,
        Guid venueId,
        Guid bookingId,
        string? reason = null,
        bool? paymentReceived = null) =>
        await ReadAsync(await CancelAsync(client, venueId, bookingId, reason, paymentReceived));

    private static Task<VenueBookingResponse> ReadAsync(HttpResponseMessage response) =>
        VenueScenario.ReadAsync<VenueBookingResponse>(response);
}
