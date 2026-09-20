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

    /// <summary>
    /// The one that matters: a double click, a retry, or two people at the counter. Both requests
    /// read what is owed before either has written, so without the database holding the line both
    /// find room for the whole of it and the booking ends up with twice its debt written off
    /// against it, in records nobody can edit (PRD BR-06).
    /// </summary>
    [Fact]
    public async Task Two_people_sending_the_whole_of_it_at_once_write_it_down_once()
    {
        var (owner, venue, booking) = await OwedInFullAsync();

        var both = await Task.WhenAll(
            SendAsync(owner, venue.Id, booking.Id, booking.TotalBaht),
            SendAsync(owner, venue.Id, booking.Id, booking.TotalBaht));

        Assert.Single(both, answer => answer.StatusCode == HttpStatusCode.OK);
        var refused = Assert.Single(both, answer => answer.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(RefundErrorCodes.MoreThanIsOwed, await refused.ErrorCodeAsync());

        var after = await ReadAsync(owner, venue.Id, booking.Id);
        Assert.Equal(booking.TotalBaht, after.SentBackBaht);
        Assert.Single(after.Records);
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

    /// <summary>
    /// Enum.TryParse takes more than the names: a number lands on whatever it numbers, a different
    /// case is the same value to it, and a comma is a bitwise or. None of those is a way of paying
    /// anybody, and the record would keep whichever one was sent (PRD US-18).
    /// </summary>
    [Theory]
    [InlineData("Cheque")]
    [InlineData("1")]
    [InlineData("transfer")]
    [InlineData("Transfer,Cash")]
    [InlineData("")]
    public async Task A_way_of_paying_the_venue_did_not_name_is_refused(string method)
    {
        var (owner, venue, booking) = await OwedInFullAsync();

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds",
            new RecordRefundRequest(100m, VenueScenario.Today, method, null));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(RefundErrorCodes.MethodNotAllowed, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_note_longer_than_the_column_is_refused_rather_than_cut_short()
    {
        var (owner, venue, booking) = await OwedInFullAsync();
        var tooLong = new string('ก', RefundRecord.NoteMaxLength + 1);

        var written = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds",
            new RecordRefundRequest(100m, VenueScenario.Today, nameof(RefundMethod.Transfer), tooLong));
        Assert.Equal(HttpStatusCode.BadRequest, written.StatusCode);
        Assert.Equal(RefundErrorCodes.NoteTooLong, await written.ErrorCodeAsync());

        var record = Assert.Single((await RecordAsync(owner, venue.Id, booking.Id, 100m)).Records);
        var voided = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds/{record.Id}/void",
            new VoidRefundRequest(tooLong));
        Assert.Equal(HttpStatusCode.BadRequest, voided.StatusCode);
        Assert.Equal(RefundErrorCodes.NoteTooLong, await voided.ErrorCodeAsync());
    }

    /// <summary>
    /// The counter's own day carries the same two numbers as the panel, because the row is what
    /// the venue reads first and it has to agree with what it opens (PRD 6.2).
    /// </summary>
    [Fact]
    public async Task The_venue_s_day_shows_what_has_gone_back_and_what_is_left()
    {
        var (owner, venue, booking) = await OwedInFullAsync();
        await RecordAsync(owner, venue.Id, booking.Id, 100m);

        var day = await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await owner.GetAsync(
                $"/api/venues/{venue.Id}/bookings?date={VenueScenario.Today.AddDays(1):yyyy-MM-dd}"));

        var row = Assert.Single(day, seen => seen.BookingId == booking.Id);
        Assert.Equal(booking.TotalBaht, row.RefundDueBaht);
        Assert.Equal(100m, row.SentBackBaht);
        Assert.Equal(booking.TotalBaht - 100m, row.OutstandingBaht);
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
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
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
