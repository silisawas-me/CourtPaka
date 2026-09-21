using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Email;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Jobs;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>What the booker is told, and that they are told once: PRD US-06, S-05.</summary>
public sealed class BookerMailTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    private static readonly DateOnly Tomorrow = VenueScenario.Today.AddDays(1);

    [Fact]
    public async Task A_hold_tells_its_booker_what_to_pay_and_where()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, email) = await scenario.SignedInClientWithEmailAsync();
        var booking = await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));

        var mail = Assert.Single(await MailAfterSweepAsync(email, 1));

        Assert.Equal([BookerNoticeKind.Held], await KindsToldAsync(booking.Id));
        Assert.Equal(SupportedLanguages.Thai, mail.Language);
        Assert.Contains(venue.Name, mail.Subject);
        Assert.Contains($"/bookings/{booking.Id}", mail.Body);

        // In Thai the year is the Buddhist one, the way the screen shows it (PRD US-23).
        Assert.Contains((Tomorrow.Year + 543).ToString(), mail.Body);
    }

    [Fact]
    public async Task However_many_times_the_caretaker_looks_it_is_said_once()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, email) = await scenario.SignedInClientWithEmailAsync();
        await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));

        await Task.WhenAll(SweepAsync(), SweepAsync(), SweepAsync());
        await SweepAsync();

        Assert.Single(await MailAfterSweepAsync(email, 1));
    }

    [Fact]
    public async Task A_hold_that_ran_out_says_so()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, email) = await scenario.SignedInClientWithEmailAsync();
        var booking = await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));
        await scenario.LapseHoldAsync(booking.Id);

        await MailAfterSweepAsync(email, 2);

        Assert.Equal(
            [BookerNoticeKind.Held, BookerNoticeKind.Expired],
            await KindsToldAsync(booking.Id));
    }

    [Fact]
    public async Task The_venue_confirming_the_slip_is_told()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, email, booking) = await WaitingAsync(venue.Id, courts[0]);

        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);
        await MailAfterSweepAsync(email, 2);

        Assert.Equal(
            [BookerNoticeKind.Held, BookerNoticeKind.Confirmed],
            await KindsToldAsync(booking.Id));
    }

    [Fact]
    public async Task A_rejected_slip_carries_the_venue_s_reason()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, email, booking) = await WaitingAsync(venue.Id, courts[0]);

        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/reject",
            new RejectSlipRequest("ยอดในสลิปไม่ตรงกับยอดจอง", PaymentReceived: false));
        var mail = (await MailAfterSweepAsync(email, 2)).Last();

        Assert.Contains("ยอดในสลิปไม่ตรงกับยอดจอง", mail.Body);
        Assert.Contains(BookerNoticeKind.Rejected, await KindsToldAsync(booking.Id));
    }

    [Fact]
    public async Task A_booker_who_cancels_is_told_it_is_done()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, email) = await scenario.SignedInClientWithEmailAsync();
        var booking = await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));

        var cancelled = await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        await MailAfterSweepAsync(email, 2);

        Assert.Equal(
            [BookerNoticeKind.Held, BookerNoticeKind.Cancelled],
            await KindsToldAsync(booking.Id));
    }

    /// <summary>
    /// The venue cancelling says why, in the venue's words, and what comes back (PRD US-06).
    /// </summary>
    [Fact]
    public async Task A_venue_that_cancels_says_why_and_what_is_owed()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, email, booking) = await WaitingAsync(venue.Id, courts[0]);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        var cancelled = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/cancel",
            new VenueCancelRequest(
                nameof(CancellationReason.VenueInitiated), null, "หลังคารั่ว คอร์ทใช้ไม่ได้"));
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        var mail = (await MailAfterSweepAsync(email, 3)).Last();

        Assert.Contains("หลังคารั่ว คอร์ทใช้ไม่ได้", mail.Body);
        // All of it goes back when the venue could not honour the booking.
        Assert.Contains(booking.TotalBaht.ToString("#,0.##"), mail.Body);
    }

    [Fact]
    public async Task A_refund_written_down_is_told_to_the_booker()
    {
        var (owner, venue, booking, email) = await OwedInFullAsync();

        await RecordRefundAsync(owner, venue.Id, booking.Id, booking.TotalBaht);
        await MailAfterSweepAsync(email, 3);

        Assert.Contains(BookerNoticeKind.RefundRecorded, await KindsToldAsync(booking.Id));
    }

    /// <summary>A record the venue took back before anybody looked is not news (PRD US-18).</summary>
    [Fact]
    public async Task But_not_one_taken_back_before_it_was_told()
    {
        var (owner, venue, booking, email) = await OwedInFullAsync();
        await MailAfterSweepAsync(email, 2);

        var record = await RecordRefundAsync(owner, venue.Id, booking.Id, booking.TotalBaht);
        var voided = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds/{record}/void",
            new VoidRefundRequest("โอนผิดบัญชี"));
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        await SweepAsync();

        Assert.Equal(2, Told(email).Count);
    }

    [Fact]
    public async Task A_confirmed_booker_is_reminded_two_hours_before_play()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, email, booking) = await WaitingAsync(venue.Id, courts[0]);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);
        await MailAfterSweepAsync(email, 2);

        await scenario.StartsInAsync(booking.Id, TimeSpan.FromHours(3));
        await SweepAsync();
        Assert.DoesNotContain(BookerNoticeKind.AboutToPlay, await KindsToldAsync(booking.Id));

        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(90));
        await MailAfterSweepAsync(email, 3);
        await SweepAsync();

        Assert.Single(await KindsToldAsync(booking.Id), kind => kind == BookerNoticeKind.AboutToPlay);
        Assert.Equal(3, Told(email).Count);
    }

    [Fact]
    public async Task A_booking_that_is_only_held_is_not_reminded()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (_, email, booking) = await WaitingAsync(venue.Id, courts[0]);
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(90));

        await MailAfterSweepAsync(email, 1);
        await SweepAsync();

        Assert.DoesNotContain(BookerNoticeKind.AboutToPlay, await KindsToldAsync(booking.Id));
    }

    /// <summary>The counter's customer has no account, so there is nobody to write to (US-06).</summary>
    [Fact]
    public async Task A_counter_booking_tells_nobody()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var taken = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings",
                new CounterBookingRequest(
                    [new BookingSlotRequest(courts[0], Tomorrow, 18)], "คุณสมชาย", null, "Cash")),
            HttpStatusCode.Created);
        await scenario.StartsInAsync(taken.BookingId, TimeSpan.FromMinutes(90));

        await SweepAsync();

        Assert.Empty(await KindsToldAsync(taken.BookingId));
    }

    [Fact]
    public async Task A_booker_who_reads_english_is_written_to_in_english()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, email) = await scenario.SignedInClientWithEmailAsync();
        var changed = await booker.PutAsJsonAsync(
            "/api/auth/me/language", new ChangeLanguageRequest(SupportedLanguages.English));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));

        var mail = Assert.Single(await MailAfterSweepAsync(email, 1));

        Assert.Equal(SupportedLanguages.English, mail.Language);
        Assert.Contains(Tomorrow.Year.ToString(), mail.Body);
        Assert.DoesNotContain((Tomorrow.Year + 543).ToString(), mail.Body);
    }

    /// <summary>
    /// Every kind has words in both languages. The switches have no default arm on purpose, so
    /// a kind added without words throws here rather than in the caretaker at night.
    /// </summary>
    [Theory]
    [InlineData(SupportedLanguages.Thai)]
    [InlineData(SupportedLanguages.English)]
    public void Every_kind_has_words(string language)
    {
        foreach (var kind in Enum.GetValues<BookerNoticeKind>())
        {
            var (subject, body) = BookerLetters.Write(
                new BookerLetter(
                    kind,
                    "สนามทดสอบ",
                    [new LetterSlot("คอร์ท 1", new DateTimeOffset(2026, 9, 26, 11, 0, 0, TimeSpan.Zero))],
                    400m,
                    "https://example.test/bookings/1",
                    new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero)),
                language);

            Assert.Contains("สนามทดสอบ", subject);
            Assert.Contains("https://example.test/bookings/1", body);

            // Played at the venue's hour, not the server's: 11:00 UTC is 18:00 in Bangkok.
            Assert.Contains("18:00–19:00", body);
        }
    }

    // ---------------------------------------------------------------------------------------

    private async Task<(HttpClient Booker, string Email, BookingResponse Booking)> WaitingAsync(
        Guid venueId,
        Guid courtId)
    {
        var (booker, email) = await scenario.SignedInClientWithEmailAsync();
        var booking = await VenueScenario.HoldAsync(booker, venueId, Tomorrow, (courtId, 18));
        var sent = await VenueScenario.UploadAsync(booker, booking.Id, VenueScenario.Jpeg());
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        return (booker, email, booking);
    }

    /// <summary>A booking the venue owes all of back: the slip was turned away after the money came.</summary>
    private async Task<(HttpClient Owner, VenueResponse Venue, BookingResponse Booking, string Email)>
        OwedInFullAsync()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, email, booking) = await WaitingAsync(venue.Id, courts[0]);

        var turned = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/reject",
            new RejectSlipRequest("ยอดไม่ตรง", PaymentReceived: true));
        Assert.Equal(HttpStatusCode.OK, turned.StatusCode);

        return (owner, venue, booking, email);
    }

    private static async Task<Guid> RecordRefundAsync(
        HttpClient owner,
        Guid venueId,
        Guid bookingId,
        decimal amount)
    {
        var written = await VenueScenario.ReadAsync<RefundsResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/bookings/{bookingId}/refunds",
                new RecordRefundRequest(
                    amount, VenueScenario.Today, nameof(RefundMethod.Transfer), null)),
            HttpStatusCode.OK);

        return written.Records.OrderBy(record => record.RecordedAt).Last().Id;
    }

    /// <summary>One pass of the booker mail, the way the caretaker runs it.</summary>
    private async Task SweepAsync()
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await BookedSlots.ReleaseAllLapsedAsync(database, DateTimeOffset.UtcNow, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<BookerMail>()
            .SendDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);
    }

    /// <summary>
    /// Sweeps, and answers what this address has been told so far, which must be this many.
    /// Nothing else sweeps booker mail in the test run, so the count is this class's alone.
    /// </summary>
    private async Task<IReadOnlyList<EmailMessage>> MailAfterSweepAsync(string email, int expected)
    {
        await SweepAsync();

        var mail = Told(email);
        Assert.Equal(expected, mail.Count);
        return mail;
    }

    /// <summary>
    /// What this address was told about its bookings. Signing up sent it a verification message
    /// too, which is not this story's.
    /// </summary>
    private List<EmailMessage> Told(string email) =>
        [.. api.Emails.To(email).Where(message => message.Body.Contains("/bookings/"))];

    /// <summary>What this booking's booker has been told about, in the order it was claimed.</summary>
    private async Task<BookerNoticeKind[]> KindsToldAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.BookerNotices
            .AsNoTracking()
            .Where(notice => notice.BookingId == bookingId)
            .OrderBy(notice => notice.Id)
            .Select(notice => notice.Kind)
            .ToArrayAsync();
    }
}
