using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>What the venue has actually sent back: PRD US-18, 6.2, BR-06.</summary>
public sealed class RefundTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task A_booking_that_owes_nothing_has_nothing_to_send_back()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        var refunds = await ReadAsync(owner, venue.Id, booking.Id);

        Assert.Equal(0m, refunds.RefundDueBaht);
        Assert.Equal(0m, refunds.OutstandingBaht);
        Assert.Empty(refunds.Records);
    }

    [Fact]
    public async Task What_is_sent_back_can_be_written_down_in_parts_until_it_is_whole()
    {
        var (owner, venue, booking) = await OwedInFullAsync();

        var half = await RecordAsync(owner, venue.Id, booking.Id, booking.TotalBaht / 2);
        Assert.Equal(booking.TotalBaht / 2, half.SentBackBaht);
        Assert.Equal(booking.TotalBaht / 2, half.OutstandingBaht);

        var rest = await RecordAsync(owner, venue.Id, booking.Id, booking.TotalBaht / 2);
        Assert.Equal(booking.TotalBaht, rest.SentBackBaht);
        Assert.Equal(0m, rest.OutstandingBaht);
        Assert.Equal(2, rest.Records.Length);
    }

    [Fact]
    public async Task Nobody_can_write_down_more_than_the_booking_owes()
    {
        var (owner, venue, booking) = await OwedInFullAsync();

        var refused = await SendAsync(owner, venue.Id, booking.Id, booking.TotalBaht + 1);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(RefundErrorCodes.MoreThanIsOwed, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Nor_once_the_whole_of_it_has_been_sent()
    {
        var (owner, venue, booking) = await OwedInFullAsync();
        await RecordAsync(owner, venue.Id, booking.Id, booking.TotalBaht);

        var refused = await SendAsync(owner, venue.Id, booking.Id, 1m);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(RefundErrorCodes.MoreThanIsOwed, await refused.ErrorCodeAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task An_amount_that_is_not_money_going_out_is_refused(decimal amount)
    {
        var (owner, venue, booking) = await OwedInFullAsync();

        var refused = await SendAsync(owner, venue.Id, booking.Id, amount);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(RefundErrorCodes.AmountNotPositive, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_transfer_dated_tomorrow_has_not_happened_yet()
    {
        var (owner, venue, booking) = await OwedInFullAsync();

        var refused = await SendAsync(
            owner, venue.Id, booking.Id, 100m, on: VenueScenario.Today.AddDays(1));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(RefundErrorCodes.NotYetSent, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_way_of_paying_the_venue_did_not_name_is_refused()
    {
        var (owner, venue, booking) = await OwedInFullAsync();

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds",
            new RecordRefundRequest(100m, VenueScenario.Today, "Cheque", null));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(RefundErrorCodes.MethodNotAllowed, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task The_owner_can_take_a_record_back_and_what_is_owed_returns()
    {
        var (owner, venue, booking) = await OwedInFullAsync();
        var written = await RecordAsync(owner, venue.Id, booking.Id, booking.TotalBaht);
        var record = Assert.Single(written.Records);

        var after = await ReadAsync(await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds/{record.Id}/void",
            new VoidRefundRequest("โอนไม่สำเร็จ ธนาคารตีกลับ")));

        Assert.Equal(0m, after.SentBackBaht);
        Assert.Equal(booking.TotalBaht, after.OutstandingBaht);

        // The record stays: what the venue said at the time is part of the history (PRD US-18).
        var voided = Assert.Single(after.Records);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal("โอนไม่สำเร็จ ธนาคารตีกลับ", voided.VoidReason);
    }

    [Fact]
    public async Task Taking_a_record_back_needs_a_reason_and_needs_the_owner()
    {
        var (owner, venue, booking) = await OwedInFullAsync();
        var record = Assert.Single(
            (await RecordAsync(owner, venue.Id, booking.Id, 100m)).Records);

        var noReason = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds/{record.Id}/void",
            new VoidRefundRequest("   "));
        Assert.Equal(BookingErrorCodes.ReasonRequired, await noReason.ErrorCodeAsync());

        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageBookings));
        var notOwner = await staff.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds/{record.Id}/void",
            new VoidRefundRequest("กดผิด"));
        Assert.Equal(HttpStatusCode.Forbidden, notOwner.StatusCode);
        Assert.Equal(BookingErrorCodes.OwnerOnly, await notOwner.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_record_cannot_be_taken_back_twice()
    {
        var (owner, venue, booking) = await OwedInFullAsync();
        var record = Assert.Single((await RecordAsync(owner, venue.Id, booking.Id, 100m)).Records);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds/{record.Id}/void",
            new VoidRefundRequest("กดผิด"));

        var again = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds/{record.Id}/void",
            new VoidRefundRequest("กดผิดอีก"));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(RefundErrorCodes.AlreadyVoided, await again.ErrorCodeAsync());
    }

    [Fact]
    public async Task Staff_who_cannot_handle_bookings_cannot_write_one_down()
    {
        var (owner, venue, booking) = await OwedInFullAsync();
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.VerifySlip));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await SendAsync(staff, venue.Id, booking.Id, 100m)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await staff.GetAsync(
                $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds")).StatusCode);
    }

    [Fact]
    public async Task Another_venue_cannot_write_against_this_one_s_booking()
    {
        var (_, venue, booking) = await OwedInFullAsync();
        var (stranger, elsewhere, _) = await scenario.BookableVenueAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await SendAsync(stranger, elsewhere.Id, booking.Id, 100m)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await SendAsync(stranger, venue.Id, booking.Id, 100m)).StatusCode);
    }

    [Fact]
    public async Task What_has_been_sent_back_is_what_the_booker_is_shown()
    {
        var (owner, venue, booking) = await OwedInFullAsync();
        await RecordAsync(owner, venue.Id, booking.Id, 100m);

        var mine = await VenueScenario.ReadAsync<BookingHistoryResponse>(
            await _booker!.GetAsync("/api/bookings"));

        var seen = Assert.Single(mine.Past, row => row.Id == booking.Id);
        Assert.Equal(booking.TotalBaht, seen.RefundDueBaht);
        Assert.Equal(100m, seen.RefundedBaht);
    }

    /// <summary>A booking the venue turned away after the money had arrived, so it owes all of it.</summary>
    private async Task<(HttpClient Owner, VenueResponse Venue, BookingResponse Booking)>
        OwedInFullAsync()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var turned = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/reject",
            new RejectSlipRequest("ยอดไม่ตรง", PaymentReceived: true));
        Assert.Equal(HttpStatusCode.OK, turned.StatusCode);

        _booker = booker;
        return (owner, venue, booking);
    }

    /// <summary>The booker of the booking the last setup made, for the one check that asks them.</summary>
    private HttpClient? _booker;

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Guid venueId,
        Guid bookingId,
        decimal amount,
        DateOnly? on = null) =>
        client.PostAsJsonAsync(
            $"/api/venues/{venueId}/bookings/{bookingId}/refunds",
            new RecordRefundRequest(
                amount,
                on ?? VenueScenario.Today,
                nameof(RefundMethod.Transfer),
                "คืนผ่านพร้อมเพย์"));

    private static async Task<RefundsResponse> RecordAsync(
        HttpClient client,
        Guid venueId,
        Guid bookingId,
        decimal amount) =>
        await ReadAsync(await SendAsync(client, venueId, bookingId, amount));

    private static async Task<RefundsResponse> ReadAsync(
        HttpClient client,
        Guid venueId,
        Guid bookingId) =>
        await ReadAsync(
            await client.GetAsync($"/api/venues/{venueId}/bookings/{bookingId}/refunds"));

    private static Task<RefundsResponse> ReadAsync(HttpResponseMessage response) =>
        VenueScenario.ReadAsync<RefundsResponse>(response);
}
