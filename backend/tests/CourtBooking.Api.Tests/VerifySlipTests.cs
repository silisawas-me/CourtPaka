using System.Net;
using System.Net.Http.Headers;
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
    public async Task The_queue_holds_what_is_waiting_oldest_first()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var first = await WaitingBookingAsync(venue.Id, courts[0], 18);
        var second = await WaitingBookingAsync(venue.Id, courts[1], 19);

        var queue = await QueueAsync(owner, venue.Id);

        Assert.Equal([first.Booking.Id, second.Booking.Id], queue.Select(item => item.BookingId));
        Assert.All(queue, item => Assert.Equal(200m, item.TotalBaht));
        Assert.All(queue, item => Assert.False(item.SameSlipSeenBefore));
    }

    [Fact]
    public async Task A_booking_about_to_be_played_is_marked_as_such()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await WaitingBookingAsync(venue.Id, courts[0], 18);

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
        var bytes = Jpeg();
        await WaitingBookingAsync(venue.Id, courts[0], 18, bytes);
        await WaitingBookingAsync(venue.Id, courts[1], 19, bytes);

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
        var waiting = await WaitingBookingAsync(venue.Id, courts[0], 18);

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
        var waiting = await WaitingBookingAsync(venue.Id, courts[0], 18);

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
        var waiting = await WaitingBookingAsync(venue.Id, courts[0], 18);

        var rejected = await RejectAsync(
            owner, venue.Id, waiting.Booking.Id, "หาไม่เจอในบัญชี", paymentReceived: false);

        Assert.Equal(nameof(PaymentState.NotReceived), rejected.PaymentState);
        Assert.Equal(0m, rejected.RefundDueBaht);
    }

    [Fact]
    public async Task A_rejected_booking_gives_its_hours_back()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var waiting = await WaitingBookingAsync(venue.Id, courts[0], 18);

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
        var waiting = await WaitingBookingAsync(venue.Id, courts[0], 18);

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
        var waiting = await WaitingBookingAsync(venue.Id, courts[0], 18);

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
        var waiting = await WaitingBookingAsync(venue.Id, courts[0], 18);

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
        var waiting = await WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/confirm", null);

        var again = await owner.PostAsync(
            $"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/confirm", null);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(SlipErrorCodes.NotAwaitingVerification, await again.ErrorCodeAsync());
    }

    [Fact]
    public async Task The_venue_reads_the_slip_of_its_own_booking()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var bytes = Jpeg();
        var waiting = await WaitingBookingAsync(venue.Id, courts[0], 18, bytes);

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
        var waiting = await WaitingBookingAsync(venue.Id, courts[0], 18);
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
        var waiting = await WaitingBookingAsync(venue.Id, courts[0], 18);
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
        var waiting = await WaitingBookingAsync(venue.Id, courts[0], 18);
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.VerifySlip));

        var confirmed = await VenueScenario.ReadAsync<BookingResponse>(
            await staff.PostAsync(
                $"/api/venues/{venue.Id}/slip-queue/{waiting.Booking.Id}/confirm", null));

        Assert.Equal(nameof(BookingStatus.Confirmed), confirmed.Status);
    }

    private static byte[] Jpeg() =>
        [0xFF, 0xD8, 0xFF, 0xE0, .. "JFIF"u8, .. Guid.CreateVersion7().ToByteArray()];

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

    /// <summary>A booking that has been paid for and is waiting for the venue to look at it.</summary>
    private async Task<(HttpClient Booker, BookingResponse Booking)> WaitingBookingAsync(
        Guid venueId,
        Guid courtId,
        int hour,
        byte[]? slip = null)
    {
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(
            booker, venueId, VenueScenario.Today.AddDays(1), (courtId, hour));

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(slip ?? Jpeg());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "slip.jpg");

        var sent = await booker.PostAsync($"/api/bookings/{booking.Id}/slip", form);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        return (booker, booking);
    }
}
