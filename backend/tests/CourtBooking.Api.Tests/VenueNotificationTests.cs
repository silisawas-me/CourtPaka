using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Email;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>What a venue is told is waiting, and who is told: PRD US-17.</summary>
public sealed class VenueNotificationTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task A_venue_with_nothing_waiting_is_told_so()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        var waiting = await AttentionAsync(owner, venue.Id);

        Assert.Equal(0, waiting.SlipsToCheck);
        Assert.Equal(0, waiting.BookingsWithMoneyWaiting);
    }

    [Fact]
    public async Task A_slip_that_has_arrived_is_counted_and_only_those_who_may_check_it_are_told()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var checker = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.VerifySlip));
        var doorman = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.CloseCourt));

        var before = await WrittenToAsync(venue.Id);
        await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        Assert.Equal(1, (await AttentionAsync(owner, venue.Id)).SlipsToCheck);

        // Told: the owner, who holds every permission (PRD 8), and the one who checks slips.
        // Not told: the one who can only close courts, who can do nothing about a slip.
        var told = await SinceAsync(before, venue.Id);
        Assert.Equal(3, told.Count);
        Assert.Equal(2, told.Count(member => member.Value.Count == 1));
        Assert.Single(told, member => member.Value.Count == 0);

        // The number follows the same rule as the mail.
        Assert.Equal(1, (await AttentionAsync(checker, venue.Id)).SlipsToCheck);
        Assert.Equal(0, (await AttentionAsync(doorman, venue.Id)).SlipsToCheck);
    }

    [Fact]
    public async Task A_slip_seen_before_says_so_where_the_reader_will_see_it()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var bytes = VenueScenario.Jpeg();

        var before = await WrittenToAsync(venue.Id);
        await scenario.WaitingBookingAsync(venue.Id, courts[0], 18, bytes);
        await scenario.WaitingBookingAsync(venue.Id, courts[1], 19, bytes);

        var told = Assert.Single(await SinceAsync(before, venue.Id)).Value;

        Assert.Equal(2, told.Count);
        Assert.Equal(VenueLetters.TemplateOf(VenueNotice.SlipWaiting), told[0].Template);
        Assert.Equal(VenueLetters.TemplateOf(VenueNotice.SlipSeenBefore), told[1].Template);
    }

    [Fact]
    public async Task Somebody_who_has_turned_slip_mail_off_is_not_written_to()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        var chosen = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/notifications", new NotificationPreferenceRequest(false));
        Assert.Equal(HttpStatusCode.NoContent, chosen.StatusCode);

        var before = await WrittenToAsync(venue.Id);
        await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        Assert.Empty(Assert.Single(await SinceAsync(before, venue.Id)).Value);
        // Turning the mail off does not hide the work: the number is still there.
        Assert.Equal(1, (await AttentionAsync(owner, venue.Id)).SlipsToCheck);
    }

    [Fact]
    public async Task The_notice_about_money_cannot_be_turned_off()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/notifications", new NotificationPreferenceRequest(false));

        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var before = await WrittenToAsync(venue.Id);
        await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null);

        // Only the everyday notice can be silenced; the ones that mean something is stuck cannot
        // (PRD US-17).
        var told = Assert.Single(Assert.Single(await SinceAsync(before, venue.Id)).Value);
        Assert.Equal(VenueLetters.TemplateOf(VenueNotice.PaymentUnanswered), told.Template);
    }

    [Fact]
    public async Task Money_left_unanswered_is_counted_and_only_those_who_answer_are_told()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var handler = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageBookings));
        var checker = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.VerifySlip));
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var before = await WrittenToAsync(venue.Id);
        await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null);

        Assert.Equal(1, (await AttentionAsync(owner, venue.Id)).BookingsWithMoneyWaiting);

        // Told: the owner and the one who handles bookings. Not told: the one who only checks
        // slips, who cannot answer for money (PRD US-14, US-17).
        var told = await SinceAsync(before, venue.Id);
        Assert.Equal(3, told.Count);
        Assert.Equal(2, told.Count(member => member.Value.Count == 1));
        Assert.Single(told, member => member.Value.Count == 0);
        Assert.All(
            told.Values.Where(mail => mail.Count == 1),
            mail => Assert.Equal(VenueLetters.TemplateOf(VenueNotice.PaymentUnanswered), mail[0].Template));

        // And the number follows the same rule as the mail.
        Assert.Equal(1, (await AttentionAsync(handler, venue.Id)).BookingsWithMoneyWaiting);
        Assert.Equal(0, (await AttentionAsync(checker, venue.Id)).BookingsWithMoneyWaiting);
    }

    [Fact]
    public async Task Turning_a_paid_booking_away_tells_the_venue_it_owes_the_money_back()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var before = await WrittenToAsync(venue.Id);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/reject",
            new RejectSlipRequest("ยอดไม่ตรง", PaymentReceived: true));

        var told = Assert.Single(Assert.Single(await SinceAsync(before, venue.Id)).Value);
        Assert.Equal(VenueLetters.TemplateOf(VenueNotice.RefundOwed), told.Template);
        Assert.Equal(1, (await AttentionAsync(owner, venue.Id)).BookingsWithMoneyWaiting);
    }

    [Fact]
    public async Task Turning_one_away_with_no_money_in_it_leaves_nothing_to_tell()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var before = await WrittenToAsync(venue.Id);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/reject",
            new RejectSlipRequest("หาไม่เจอในบัญชี", PaymentReceived: false));

        // Nothing is owed, so there is nothing for anybody to do about it (PRD 6.2).
        Assert.Empty(Assert.Single(await SinceAsync(before, venue.Id)).Value);
        Assert.Equal(0, (await AttentionAsync(owner, venue.Id)).BookingsWithMoneyWaiting);
    }

    [Fact]
    public async Task Sending_the_money_back_clears_the_count_and_voiding_it_brings_it_back()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/reject",
            new RejectSlipRequest("ยอดไม่ตรง", PaymentReceived: true));
        Assert.Equal(1, (await AttentionAsync(owner, venue.Id)).BookingsWithMoneyWaiting);

        var written = await VenueScenario.ReadAsync<RefundsResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds",
                new RecordRefundRequest(
                    booking.TotalBaht,
                    VenueScenario.Today,
                    nameof(RefundMethod.Transfer),
                    null)));

        // Paid back is finished with: a number nobody can clear is only a reproach (PRD US-17).
        Assert.Equal(0, (await AttentionAsync(owner, venue.Id)).BookingsWithMoneyWaiting);

        var record = Assert.Single(written.Records);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds/{record.Id}/void",
            new VoidRefundRequest("โอนไม่สำเร็จ ธนาคารตีกลับ"));

        Assert.Equal(1, (await AttentionAsync(owner, venue.Id)).BookingsWithMoneyWaiting);
    }

    [Fact]
    public async Task Answering_for_the_money_takes_the_booking_off_the_count()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null);

        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/settle-payment",
            new SettlePaymentRequest(false));

        // Nothing arrived, so nothing is owed either (PRD 6.2).
        Assert.Equal(0, (await AttentionAsync(owner, venue.Id)).BookingsWithMoneyWaiting);
    }

    [Fact]
    public async Task Saying_the_money_did_arrive_is_what_creates_the_debt_and_it_says_so()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null);

        var before = await WrittenToAsync(venue.Id);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/settle-payment",
            new SettlePaymentRequest(true));

        // The count does not change — the booking only moves from one half of it to the other —
        // so the message is the only thing that says a transfer is now owed (PRD US-17, BR-06).
        Assert.Equal(1, (await AttentionAsync(owner, venue.Id)).BookingsWithMoneyWaiting);
        var told = Assert.Single(Assert.Single(await SinceAsync(before, venue.Id)).Value);
        Assert.Equal(VenueLetters.TemplateOf(VenueNotice.RefundOwed), told.Template);
    }

    [Fact]
    public async Task A_venue_cancelling_a_paid_booking_itself_is_told_it_owes_the_money_back()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        var before = await WrittenToAsync(venue.Id);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/cancel",
            new VenueCancelRequest(nameof(CancellationReason.VenueInitiated), null, null));

        var told = Assert.Single(Assert.Single(await SinceAsync(before, venue.Id)).Value);
        Assert.Equal(VenueLetters.TemplateOf(VenueNotice.RefundOwed), told.Template);
    }

    [Fact]
    public async Task A_venue_that_may_not_act_is_neither_told_nor_shown_anything()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        Assert.Equal(1, (await AttentionAsync(owner, venue.Id)).SlipsToCheck);

        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);

        // Every door these numbers sit on is refused to a suspended venue, so a count it cannot
        // clear would be only a reproach (PRD US-20).
        var waiting = await AttentionAsync(owner, venue.Id);
        Assert.Equal(0, waiting.SlipsToCheck);
        Assert.Equal(0, waiting.BookingsWithMoneyWaiting);

        // And a better picture of the same slip tells nobody anything either.
        var before = await WrittenToAsync(venue.Id);
        await VenueScenario.UploadAsync(await scenario.SignedInClientAsync(), booking.Id, VenueScenario.Jpeg());
        Assert.Empty(Assert.Single(await SinceAsync(before, venue.Id)).Value);
    }

    [Fact]
    public async Task Turning_the_mail_off_at_one_venue_leaves_the_other_alone()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (elsewhere, other, otherCourts) = await scenario.BookableVenueAsync();

        // The same person at both counters, which is the case the per-venue choice exists for.
        await scenario.InviteAndAcceptAsync(
            elsewhere, owner, other.Id, await OwnerEmailAsync(venue.Id));

        await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/notifications", new NotificationPreferenceRequest(false));

        var before = await WrittenToAsync(venue.Id);
        var (_, here) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var (_, there) = await scenario.WaitingBookingAsync(other.Id, otherCourts[0], 18);

        // One inbox, two venues, so the bookings the mail names are what tells them apart.
        var mail = Assert.Single(await SinceAsync(before, venue.Id)).Value;

        Assert.DoesNotContain(mail, message => message.Body.Contains(here.Id.ToString()));
        Assert.Contains(mail, message => message.Body.Contains(there.Id.ToString()));
    }

    [Fact]
    public async Task Another_venue_is_told_nothing_of_this_one()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (stranger, elsewhere, _) = await scenario.BookableVenueAsync();

        var before = await WrittenToAsync(elsewhere.Id);
        await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        Assert.Empty(Assert.Single(await SinceAsync(before, elsewhere.Id)).Value);
        Assert.Equal(0, (await AttentionAsync(stranger, elsewhere.Id)).SlipsToCheck);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.GetAsync($"/api/venues/{venue.Id}/attention")).StatusCode);
    }

    /// <summary>What each member of a venue has been written to, newest last.</summary>
    private sealed class Mail : Dictionary<Guid, IReadOnlyList<EmailMessage>>;

    /// <summary>
    /// The mail each member of this venue has. By address rather than by counting what the sender
    /// holds: one sender is shared by the whole run, and classes run alongside each other.
    /// </summary>
    private async Task<Mail> WrittenToAsync(Guid venueId)
    {
        var mail = new Mail();
        foreach (var (userId, address) in await scenario.MemberEmailsAsync(venueId))
        {
            mail[userId] = api.Emails.To(address);
        }

        return mail;
    }

    /// <summary>
    /// What was written since, per member. Signing up and being invited each send mail of their
    /// own, so what a test is about is the difference rather than the pile.
    /// </summary>
    private async Task<Mail> SinceAsync(Mail before, Guid venueId)
    {
        var since = new Mail();
        foreach (var (userId, mail) in await WrittenToAsync(venueId))
        {
            var already = before.TryGetValue(userId, out var had) ? had.Count : 0;
            since[userId] = [.. mail.Skip(already)];
        }

        return since;
    }

    private async Task<string> OwnerEmailAsync(Guid venueId) =>
        (await scenario.MemberEmailsAsync(venueId)).Values.Single();

    private static async Task<VenueAttentionResponse> AttentionAsync(
        HttpClient client,
        Guid venueId) =>
        await VenueScenario.ReadAsync<VenueAttentionResponse>(
            await client.GetAsync($"/api/venues/{venueId}/attention"));
}
