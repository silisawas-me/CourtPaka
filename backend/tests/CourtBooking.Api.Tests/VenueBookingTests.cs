using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Localization;
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
    public async Task Staff_without_the_permission_see_none_of_the_day()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.VerifySlip));

        // The day names every booker by the address they signed up with, so it is the permission
        // holders' and not the venue's members at large (PDPA, PRD 8, US-13).
        var read = await staff.GetAsync(
            $"/api/venues/{venue.Id}/bookings?date={VenueScenario.Today.AddDays(1):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
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

    [Theory]
    [InlineData(2, false)]
    [InlineData(30, true)]
    public async Task A_customer_asking_gets_what_the_terms_they_booked_under_give(
        int hoursOfNotice,
        bool allOfIt)
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();

        // The venue's default terms turn at a day's notice (PRD S-11), so this stands either
        // side of it rather than on a calendar date, which would move with the hour it is run at.
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromHours(hoursOfNotice));

        var cancelled = await CancelledAsync(
            owner, venue.Id, booking.Id, reason: nameof(CancellationReason.CustomerRequest));

        Assert.Equal(allOfIt ? booking.TotalBaht : 0m, cancelled.RefundDueBaht);
        Assert.Equal(nameof(PaymentState.Received), cancelled.PaymentState);
    }

    [Fact]
    public async Task What_each_answer_would_give_back_is_named_before_anything_is_pressed()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromHours(30));

        var offered = Assert.Single(
            await DayAsync(owner, venue.Id, DayOf(TimeSpan.FromHours(30))),
            row => row.BookingId == booking.Id);

        // All three are open on a live booking, each with the amount it settles (PRD US-13).
        Assert.Equal(
            [
                nameof(CancellationReason.CustomerRequest),
                nameof(CancellationReason.VenueInitiated),
                nameof(CancellationReason.PaymentNotReceived),
            ],
            offered.Can.CancelChoices.Select(choice => choice.Reason));

        Assert.Equal(booking.TotalBaht, offered.Can.CancelChoices[0].RefundBaht);
        Assert.Equal(booking.TotalBaht, offered.Can.CancelChoices[1].RefundBaht);
        Assert.Equal(0m, offered.Can.CancelChoices[2].RefundBaht);
    }

    [Fact]
    public async Task Hours_already_played_are_not_given_back_at_the_customer_s_asking()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromHours(-2));

        var offered = Assert.Single(
            await DayAsync(owner, venue.Id, DayOf(TimeSpan.FromHours(-2))),
            row => row.BookingId == booking.Id);

        // The counter is never offered an answer the rules would refuse (PRD 6.1).
        Assert.DoesNotContain(
            nameof(CancellationReason.CustomerRequest),
            offered.Can.CancelChoices.Select(choice => choice.Reason));

        var cancelled = await CancelledAsync(
            owner, venue.Id, booking.Id, reason: nameof(CancellationReason.VenueInitiated));

        // Inside the correcting window the money still answers to the reason (PRD 6.1).
        Assert.Equal(nameof(BookingStatus.Cancelled), cancelled.Status);
        Assert.Equal(booking.TotalBaht, cancelled.RefundDueBaht);
    }

    [Theory]
    // Parses to an undefined value.
    [InlineData("7")]
    // Parses to CustomerRequest by its number rather than its name.
    [InlineData("1")]
    // Ors the two into a third reason, which would apply that reason's money while the record
    // said something else entirely.
    [InlineData("CustomerRequest,VenueInitiated")]
    [InlineData("customerrequest")]
    public async Task A_reason_that_is_not_one_of_the_three_names_is_refused(string sent)
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();

        var refused = await CancelAsync(owner, venue.Id, booking.Id, reason: sent);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.ReasonNotAllowedHere, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task The_record_says_the_reason_that_was_decided_on()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();

        await CancelledAsync(
            owner,
            venue.Id,
            booking.Id,
            reason: nameof(CancellationReason.VenueInitiated),
            note: "ไฟดับทั้งสนาม");

        var change = Assert.Single(
            await scenario.HistoryAsync(booking.Id),
            recorded => recorded.To == BookingStatus.Cancelled);

        // The record and the money have to say the same thing (PRD 6.1), and the reason is a
        // value rather than a prefix on the note — US-11 and US-15 count by it.
        Assert.Equal(CancellationReason.VenueInitiated, change.Cause);
        Assert.Equal("ไฟดับทั้งสนาม", change.Reason);
    }

    [Fact]
    public async Task A_cancellation_with_nothing_written_beside_it_still_says_why()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();

        await CancelledAsync(
            owner,
            venue.Id,
            booking.Id,
            reason: nameof(CancellationReason.CustomerRequest),
            note: null);

        var change = Assert.Single(
            await scenario.HistoryAsync(booking.Id),
            recorded => recorded.To == BookingStatus.Cancelled);

        Assert.Equal(CancellationReason.CustomerRequest, change.Cause);
        Assert.Equal(string.Empty, change.Reason);
    }

    /// <summary>
    /// Turning a slip away says why in words rather than by choosing one of the three, so the
    /// column stays empty and nothing counts that move as a cancellation reason (PRD 6.1).
    /// </summary>
    [Fact]
    public async Task A_slip_turned_away_leaves_the_reason_column_alone()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/reject",
            new RejectSlipRequest("ยอดไม่ตรง", PaymentReceived: false));

        var change = Assert.Single(
            await scenario.HistoryAsync(booking.Id),
            recorded => recorded.To == BookingStatus.Rejected);

        Assert.Null(change.Cause);
        Assert.Equal("ยอดไม่ตรง", change.Reason);
    }

    [Fact]
    public async Task A_hold_that_ran_out_is_gone_rather_than_cancellable()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[0], 18));
        await scenario.LapseHoldAsync(booking.Id);

        var refused = await CancelAsync(owner, venue.Id, booking.Id);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.NotCancellable, await refused.ErrorCodeAsync());
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
        // The venue keeps what it was paid, and every hour that is not over goes back on sale —
        // the one already running included, because a walk-in may have the rest of it
        // (PRD BR-04, US-13).
        Assert.Equal(nameof(PaymentState.Received), written.PaymentState);
        Assert.Equal(0, await scenario.HoursStillHeldAsync(booking.Id));
    }

    [Fact]
    public async Task Writing_off_a_no_show_is_not_the_owner_s_alone()
    {
        var (owner, venue, booking) = await ConfirmedTwoHoursAsync();
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-20));
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageBookings));

        // PRD 6.1 puts this row under ManageBookings, not under the owner.
        var written = await ReadAsync(await staff.PostAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/no-show", null));

        Assert.Equal(nameof(BookingStatus.NoShow), written.Status);
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
    public async Task Who_answered_for_the_money_is_in_the_history()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null);

        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/settle-payment",
            new SettlePaymentRequest(true));

        // It moves nothing, and it decides money, so it is written down all the same (PRD 6.1).
        var answered = Assert.Single(
            await scenario.HistoryAsync(booking.Id),
            change => change.From == BookingStatus.Cancelled
                && change.To == BookingStatus.Cancelled);
        Assert.Equal(nameof(PaymentState.Received), answered.Reason);
        Assert.NotNull(answered.ChangedByUserId);
    }

    [Fact]
    public async Task A_no_show_written_by_mistake_can_be_taken_back_by_the_owner()
    {
        var (owner, venue, booking) = await ConfirmedTwoHoursAsync();
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-20));
        await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/no-show", null);
        Assert.Equal(0, await scenario.HoursStillHeldAsync(booking.Id));

        // Correcting what was recorded starts once the hours are over (PRD 6.1).
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromHours(-3));

        var corrected = await ReadAsync(await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/played",
            new PlayedAfterAllRequest("มาเล่นจริง พนักงานกดผิด")));

        Assert.Equal(nameof(BookingStatus.Completed), corrected.Status);
        // The hours are the booking's again, which is what makes the record true.
        Assert.Equal(2, await scenario.HoursStillHeldAsync(booking.Id));

        var change = Assert.Single(
            await scenario.HistoryAsync(booking.Id),
            recorded => recorded.From == BookingStatus.NoShow);
        Assert.Equal("มาเล่นจริง พนักงานกดผิด", change.Reason);
    }

    [Fact]
    public async Task A_no_show_cannot_be_taken_back_while_the_hours_are_still_running()
    {
        var (owner, venue, booking) = await ConfirmedTwoHoursAsync();
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-20));
        await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/no-show", null);

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/played",
            new PlayedAfterAllRequest("มาเล่นจริง"));

        // PRD 6.1 opens the correcting window at the moment the hours end, not before.
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.TooLateToCorrect, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Taking_a_no_show_back_needs_a_reason_and_needs_the_owner()
    {
        var (owner, venue, booking) = await ConfirmedBookingAsync();
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-20));
        await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/no-show", null);
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromHours(-3));

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

        // Written off twenty minutes in, which puts its hours back on sale, and they are taken.
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-20));
        var written = await owner.PostAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/no-show", null);
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        // The hours are moved behind them first and taken afterwards, so that what somebody else
        // holds is the hours this booking would be claiming back.
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromHours(-3));
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

    /// <summary>
    /// The venue's day a booking falls on once its hours have been moved. Tests stand at a
    /// distance from now rather than on a calendar date, so the date has to be worked out from
    /// the same clock the server reads (PRD BR-10).
    /// </summary>
    private static DateOnly DayOf(TimeSpan fromNow) =>
        PlatformRequirements.BangkokDateAndHour(DateTimeOffset.UtcNow + fromNow).Date;

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
        bool? paymentReceived = null,
        string? note = null) =>
        client.PostAsJsonAsync(
            $"/api/venues/{venueId}/bookings/{bookingId}/cancel",
            new VenueCancelRequest(reason, paymentReceived, note));

    private static async Task<VenueBookingResponse> CancelledAsync(
        HttpClient client,
        Guid venueId,
        Guid bookingId,
        string? reason = null,
        bool? paymentReceived = null,
        string? note = null) =>
        await ReadAsync(
            await CancelAsync(client, venueId, bookingId, reason, paymentReceived, note));

    private static Task<VenueBookingResponse> ReadAsync(HttpResponseMessage response) =>
        VenueScenario.ReadAsync<VenueBookingResponse>(response);
}
