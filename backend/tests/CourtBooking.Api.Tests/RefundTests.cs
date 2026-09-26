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

    /// <summary>
    /// Holding the permission is being given the work, not being trusted with any amount of the
    /// venue's money (PRD US-18). Nothing until the owner says a number, and the refusal says
    /// what the number is — because the answer is to hand the booker to somebody who can send it.
    /// </summary>
    [Fact]
    public async Task Staff_start_able_to_send_back_nothing_and_are_told_so()
    {
        var (owner, venue, booking) = await OwedInFullAsync();
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageBookings));

        var refused = await SendAsync(staff, venue.Id, booking.Id, 1m);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(RefundErrorCodes.OverTheLimit, await refused.ErrorCodeAsync());
        Assert.Equal(0m, await LimitInRefusalAsync(refused));
    }

    [Fact]
    public async Task What_the_owner_trusts_them_with_is_what_they_may_send()
    {
        var (owner, venue, booking) = await OwedInFullAsync();
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageBookings));
        var who = await MemberIdAsync(owner, venue.Id);

        await SetLimitAsync(owner, venue.Id, who, 100m);

        // A baht over is a baht over, and they are told the number to work with.
        var refused = await SendAsync(staff, venue.Id, booking.Id, 100.01m);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(100m, await LimitInRefusalAsync(refused));

        var written = await RecordAsync(staff, venue.Id, booking.Id, 100m);
        Assert.Equal(100m, written.SentBackBaht);

        // And the owner is held to nothing: there is nobody above them to raise a ceiling.
        var rest = await RecordAsync(owner, venue.Id, booking.Id, booking.TotalBaht - 100m);
        Assert.Equal(booking.TotalBaht, rest.SentBackBaht);
    }

    /// <summary>
    /// A number somebody was trusted with is as much a permission as a flag is, so it is written
    /// into the same history (PRD US-18, PRD 8).
    /// </summary>
    [Fact]
    public async Task Changing_what_they_may_send_is_recorded_like_any_other_permission()
    {
        var (owner, venue, _) = await OwedInFullAsync();
        await scenario.StaffClientAsync(owner, venue.Id, nameof(VenuePermissions.ManageBookings));
        var who = await MemberIdAsync(owner, venue.Id);

        await SetLimitAsync(owner, venue.Id, who, 250m);
        await SetLimitAsync(owner, venue.Id, who, 500m);

        var history = await scenario.MembershipHistoryAsync(venue.Id, who);
        var raised = history
            .Where(change => change.Kind == MembershipChangeKind.PermissionsChanged)
            .ToArray();

        Assert.Equal(2, raised.Length);
        Assert.Equal([0m, 250m], raised.Select(change => change.RefundLimitBefore));
        Assert.Equal([250m, 500m], raised.Select(change => change.RefundLimitAfter));
    }

    /// <summary>
    /// A caller changing only what somebody may do must not silently take away what they were
    /// trusted with — the limit is left out of such a request, and left alone (PRD US-18).
    /// </summary>
    [Fact]
    public async Task Changing_permissions_alone_leaves_the_limit_where_it_was()
    {
        var (owner, venue, booking) = await OwedInFullAsync();
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageBookings));
        var who = await MemberIdAsync(owner, venue.Id);
        await SetLimitAsync(owner, venue.Id, who, 100m);

        var changed = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/members/{who}/permissions",
            new ChangePermissionsRequest(
                [nameof(VenuePermissions.ManageBookings), nameof(VenuePermissions.VerifySlip)]));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        var written = await RecordAsync(staff, venue.Id, booking.Id, 100m);
        Assert.Equal(100m, written.SentBackBaht);
    }

    /// <summary>A number that could not be a limit at all is refused before anything is stored.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public async Task A_limit_that_is_not_an_amount_is_refused(decimal baht)
    {
        var (owner, venue, _) = await OwedInFullAsync();
        await scenario.StaffClientAsync(owner, venue.Id, nameof(VenuePermissions.ManageBookings));
        var who = await MemberIdAsync(owner, venue.Id);

        var refused = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/members/{who}/permissions",
            new ChangePermissionsRequest([nameof(VenuePermissions.ManageBookings)], baht));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(VenueErrorCodes.InvalidRefundLimit, await refused.ErrorCodeAsync());
    }

    /// <summary>The one staff member of the venue the setup just made.</summary>
    private async Task<Guid> MemberIdAsync(HttpClient owner, Guid venueId)
    {
        var members = await scenario.GetMembersAsync(owner, venueId);
        return members.Single(member => member.Role == nameof(VenueRole.Staff)).UserId;
    }

    private static async Task SetLimitAsync(
        HttpClient owner, Guid venueId, Guid userId, decimal baht)
    {
        var set = await owner.PutAsJsonAsync(
            $"/api/venues/{venueId}/members/{userId}/permissions",
            new ChangePermissionsRequest([nameof(VenuePermissions.ManageBookings)], baht));
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
    }

    /// <summary>The amount the refusal says they may send, which is the point of saying it.</summary>
    private static async Task<decimal> LimitInRefusalAsync(HttpResponseMessage refused) =>
        (await refused.Content.ReadFromJsonAsync<LimitProblem>())!.LimitBaht;

    private sealed record LimitProblem(decimal LimitBaht);

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
