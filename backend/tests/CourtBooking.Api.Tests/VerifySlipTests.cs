using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>The venue looking at a slip and deciding: PRD US-12, 6.1, 6.2.</summary>
public sealed class VerifySlipTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task The_queue_holds_what_is_waiting_soonest_to_play_first()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        // The later game's slip arrives first; the queue still puts the earlier game on top,
        // because that is the one about to be asked about at the desk (thai-fit T5).
        var later = await scenario.WaitingBookingAsync(venue.Id, courts[1], 19);
        var sooner = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var queue = await QueueAsync(owner, venue.Id);

        Assert.Equal([sooner.Booking.Id, later.Booking.Id], queue.Select(item => item.BookingId));
        Assert.All(queue, item => Assert.Equal(200m, item.TotalBaht));
        Assert.All(queue, item => Assert.False(item.SameSlipSeenBefore));
        Assert.All(queue, item => Assert.Equal(TimeSpan.FromHours(1), item.EndsAt - item.StartsAt));
        Assert.Single(queue[0].Courts);
        Assert.NotEqual(queue[0].Courts, queue[1].Courts);
    }

    [Fact]
    public async Task A_booking_about_to_be_played_is_marked_as_such()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var item = Assert.Single(await QueueAsync(owner, venue.Id));

        // Tomorrow at six is not an hour away, so nothing is urgent yet.
        Assert.False(item.PlaysSoon);
        Assert.True(item.StartsAt > DateTimeOffset.UtcNow);
        Assert.Equal(waiting.Booking.Id, item.BookingId);
    }

    [Fact]
    public async Task A_slip_the_venue_has_seen_before_is_flagged_in_the_queue()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var bytes = VenueScenario.Jpeg();
        await scenario.WaitingBookingAsync(venue.Id, courts[0], 18, bytes);
        await scenario.WaitingBookingAsync(venue.Id, courts[1], 19, bytes);

        var queue = await QueueAsync(owner, venue.Id);

        Assert.Collection(
            queue,
            first => Assert.False(first.SameSlipSeenBefore),
            second => Assert.True(second.SameSlipSeenBefore));
    }

    [Fact]
    public async Task Confirming_says_the_money_arrived_and_leaves_nothing_owed()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var confirmed = await VenueScenario.ReadAsync<BookingResponse>(
            await owner.PostAsync(
                $"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/confirm", null));

        Assert.Equal(nameof(BookingStatus.Confirmed), confirmed.Status);
        Assert.Equal(nameof(PaymentState.Received), confirmed.PaymentState);
        Assert.Equal(0m, confirmed.RefundDueBaht);
        Assert.Empty(await QueueAsync(owner, venue.Id));
    }

    [Fact]
    public async Task Rejecting_with_the_money_received_owes_all_of_it_back()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var rejected = await RejectAsync(
            owner, venue.Id, waiting.Booking.Id, "ยอดไม่ตรงกับที่ต้องจ่าย", paymentReceived: true);

        Assert.Equal(nameof(BookingStatus.Rejected), rejected.Status);
        Assert.Equal(nameof(PaymentState.Received), rejected.PaymentState);
        Assert.Equal(rejected.TotalBaht, rejected.RefundDueBaht);
    }

    [Fact]
    public async Task Rejecting_because_no_money_came_owes_nothing()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var rejected = await RejectAsync(
            owner, venue.Id, waiting.Booking.Id, "หาไม่เจอในบัญชี", paymentReceived: false);

        Assert.Equal(nameof(PaymentState.NotReceived), rejected.PaymentState);
        Assert.Equal(0m, rejected.RefundDueBaht);
    }

    [Fact]
    public async Task A_rejected_booking_gives_its_hours_back()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        await RejectAsync(owner, venue.Id, waiting.Booking.Id, "สลิปไม่ชัด", paymentReceived: false);

        // Someone else can take the hour the moment it is turned away (PRD 6.1).
        var next = await scenario.SignedInClientAsync();
        var taken = await VenueScenario.HoldAsync(
            next, venue.Id, VenueScenario.Today.AddDays(1), (courts[0], 18));
        Assert.Equal(nameof(BookingStatus.Held), taken.Status);
    }

    [Fact]
    public async Task Rejecting_has_to_say_why()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/reject",
            new RejectSlipRequest("   ", false));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(SlipErrorCodes.ReasonRequired, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_reason_longer_than_the_column_is_refused_rather_than_truncated()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/reject",
            new RejectSlipRequest(new string('น', BookingStatusChange.ReasonMaxLength + 1), false));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(SlipErrorCodes.ReasonTooLong, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task What_the_venue_decided_and_why_is_in_the_history()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        await RejectAsync(owner, venue.Id, waiting.Booking.Id, "ยอดไม่ตรง", paymentReceived: false);

        var history = await scenario.HistoryAsync(waiting.Booking.Id);
        var decision = Assert.Single(history, change => change.To == BookingStatus.Rejected);
        Assert.Equal(BookingStatus.PendingVerification, decision.From);
        Assert.Equal("ยอดไม่ตรง", decision.Reason);
        Assert.NotNull(decision.ChangedByUserId);
    }

    [Fact]
    public async Task A_booking_already_decided_cannot_be_decided_again()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/confirm", null);

        var again = await owner.PostAsync(
            $"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/confirm", null);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(SlipErrorCodes.NotAwaitingVerification, await again.ErrorCodeAsync());
    }

    [Fact]
    public async Task Confirming_hours_that_have_already_been_played_completes_them()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await scenario.PlayOutAsync(waiting.Booking.Id);

        var confirmed = await VenueScenario.ReadAsync<BookingResponse>(
            await owner.PostAsync(
                $"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/confirm", null));

        // The venue checking late does not un-play the hours (PRD 6.1).
        Assert.Equal(nameof(BookingStatus.Completed), confirmed.Status);
        Assert.Equal(nameof(PaymentState.Received), confirmed.PaymentState);
        Assert.Equal(0m, confirmed.RefundDueBaht);

        // Both moves are written down, and they chain: the venue's answer, then the clock's.
        var history = await scenario.HistoryAsync(waiting.Booking.Id);
        Assert.Equal(
            BookingStatus.PendingVerification,
            Assert.Single(history, change => change.To == BookingStatus.Confirmed).From);
        var completed = Assert.Single(history, change => change.To == BookingStatus.Completed);
        Assert.Equal(BookingStatus.Confirmed, completed.From);
        // Nobody pressed it, so nobody is named (PRD 6.1).
        Assert.Null(completed.ChangedByUserId);
    }

    [Fact]
    public async Task Two_people_deciding_at_once_leave_one_answer_and_hours_that_agree_with_it()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.VerifySlip));

        // One says the money is there, the other turns it away, and neither knows about the other.
        var answers = await Task.WhenAll(
            owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/confirm", null),
            staff.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/reject",
                new RejectSlipRequest("ยอดไม่ตรง", false)));

        Assert.Single(answers, answer => answer.StatusCode == HttpStatusCode.OK);
        var refused = Assert.Single(answers, answer => answer.StatusCode != HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(SlipErrorCodes.NotAwaitingVerification, await refused.ErrorCodeAsync());

        // The history holds the one move that happened, not both.
        var history = await scenario.HistoryAsync(waiting.Booking.Id);
        var decided = Assert.Single(history, change => change.From == BookingStatus.PendingVerification);

        // And the hours say the same thing that move does. A confirmed booking whose hours had
        // been let go would be a court sold twice over (PRD BR-04, 9.2).
        var hour = (await scenario.ReadAvailabilityAsync(
                owner, venue.Id, VenueScenario.Today.AddDays(1)))
            .Courts.Single(court => court.CourtId == courts[0])
            .Hours.Single(cell => cell.Hour == 18);

        Assert.Equal(
            decided.To == BookingStatus.Rejected
                ? nameof(HourStatus.Free)
                : nameof(HourStatus.Booked),
            hour.Status);
    }

    [Fact]
    public async Task The_venue_reads_the_slip_of_its_own_booking()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var bytes = VenueScenario.Jpeg();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18, bytes);

        var served = await owner.GetAsync(
            $"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/slip");

        Assert.Equal(bytes, await served.Content.ReadAsByteArrayAsync());
        // Handed over, never rendered in the page's own origin.
        Assert.Equal("attachment", served.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Another_venue_cannot_see_or_decide_this_one_s_bookings()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var (stranger, _, _) = await scenario.BookableVenueAsync();

        // The stranger owns a venue of their own, so they are a member of something — just not
        // of this one.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.GetAsync($"/api/venues/{venue.Id}/slip-queue")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.PostAsync(
                $"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/confirm", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.GetAsync(
                $"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/slip")).StatusCode);
    }

    [Fact]
    public async Task Staff_without_the_permission_cannot_decide()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageBookings));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await staff.PostAsync(
                $"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/confirm", null)).StatusCode);
    }

    [Fact]
    public async Task Staff_with_the_permission_can()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.VerifySlip));

        var confirmed = await VenueScenario.ReadAsync<BookingResponse>(
            await staff.PostAsync(
                $"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/confirm", null));

        Assert.Equal(nameof(BookingStatus.Confirmed), confirmed.Status);
    }

    private static async Task<SlipQueueItemResponse[]> QueueAsync(HttpClient client, Guid venueId) =>
        await VenueScenario.ReadAsync<SlipQueueItemResponse[]>(
            await client.GetAsync($"/api/venues/{venueId}/slip-queue"));

    private static async Task<BookingResponse> RejectAsync(
        HttpClient client,
        Guid venueId,
        Guid bookingId,
        string reason,
        bool paymentReceived) =>
        await VenueScenario.ReadAsync<BookingResponse>(
            await client.PostAsJsonAsync(
                $"/api/venues/{venueId}/slip-queue/{bookingId}/reject",
                new RejectSlipRequest(reason, paymentReceived)));

}
