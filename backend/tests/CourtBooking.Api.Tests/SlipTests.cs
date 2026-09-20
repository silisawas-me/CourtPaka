using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>Paying for a hold by sending a picture of the transfer: PRD US-04, BR-07.</summary>
public sealed class SlipTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task Sending_a_slip_puts_the_booking_in_the_venues_queue()
    {
        var (booker, booking) = await HeldBookingAsync();

        var answer = await UploadAsync(booker, booking.Id, Jpeg());

        var updated = await VenueScenario.ReadAsync<BookingResponse>(answer);
        Assert.Equal(nameof(BookingStatus.PendingVerification), updated.Status);
        Assert.NotNull(updated.SlipUploadedAt);
    }

    [Fact]
    public async Task A_better_picture_replaces_the_one_being_checked_and_keeps_the_first()
    {
        var (booker, booking) = await HeldBookingAsync();
        await UploadAsync(booker, booking.Id, Jpeg());

        var second = await UploadAsync(booker, booking.Id, Png());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // Both rows survive: what the venue was shown at each point is part of the record.
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slips = await database.PaymentSlips
            .Where(slip => slip.BookingId == booking.Id)
            .OrderBy(slip => slip.UploadedAt)
            .ToListAsync();

        Assert.Equal(2, slips.Count);
        Assert.Equal("image/jpeg", slips[0].ContentType);
        Assert.Equal("image/png", slips[1].ContentType);

        // And the one served back is the newest.
        var served = await booker.GetAsync($"/api/bookings/{booking.Id}/slip");
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_same_picture_at_one_venue_is_flagged_without_telling_the_booker()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var bytes = Jpeg();

        var first = await scenario.SignedInClientAsync();
        var firstBooking = await HoldAsync(first, venue.Id, courts[0], 18);
        await UploadAsync(first, firstBooking.Id, bytes);

        var second = await scenario.SignedInClientAsync();
        var secondBooking = await HoldAsync(second, venue.Id, courts[1], 18);
        var answer = await UploadAsync(second, secondBooking.Id, bytes);

        // The booker is told nothing: it is accepted exactly like any other slip (PRD BR-07).
        var updated = await VenueScenario.ReadAsync<BookingResponse>(answer);
        Assert.Equal(nameof(BookingStatus.PendingVerification), updated.Status);

        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var flagged = await database.PaymentSlips.SingleAsync(
            slip => slip.BookingId == secondBooking.Id);
        Assert.NotNull(flagged.SameBytesAsSlipId);
    }

    [Fact]
    public async Task Re_sending_the_same_picture_for_the_same_booking_is_not_a_duplicate()
    {
        var (booker, booking) = await HeldBookingAsync();
        var bytes = Jpeg();
        await UploadAsync(booker, booking.Id, bytes);

        // A booker tapping "send a different slip" and picking the same photograph again is
        // correcting themselves, not using one slip twice (PRD BR-07).
        await UploadAsync(booker, booking.Id, bytes);

        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slips = await database.PaymentSlips
            .Where(slip => slip.BookingId == booking.Id)
            .ToListAsync();

        Assert.Equal(2, slips.Count);
        Assert.All(slips, slip => Assert.Null(slip.SameBytesAsSlipId));
    }

    [Fact]
    public async Task A_booking_already_marked_expired_still_says_to_contact_the_venue()
    {
        var (booker, booking) = await HeldBookingAsync();
        await scenario.LapseHoldAsync(booking.Id);
        // Someone else takes the hours, which is what writes the hold off.
        await scenario.ExpireLapsedHoldsAsync();

        var refused = await UploadAsync(booker, booking.Id, Jpeg());

        // Not "this booking is not waiting for payment": the booker may have already transferred.
        Assert.Equal(SlipErrorCodes.HoldExpired, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task The_same_picture_at_another_venue_is_not_flagged()
    {
        var bytes = Jpeg();

        var (_, firstVenue, firstCourts) = await scenario.BookableVenueAsync();
        var first = await scenario.SignedInClientAsync();
        var firstBooking = await HoldAsync(first, firstVenue.Id, firstCourts[0], 18);
        await UploadAsync(first, firstBooking.Id, bytes);

        var (_, otherVenue, otherCourts) = await scenario.BookableVenueAsync();
        var second = await scenario.SignedInClientAsync();
        var secondBooking = await HoldAsync(second, otherVenue.Id, otherCourts[0], 18);
        await UploadAsync(second, secondBooking.Id, bytes);

        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slip = await database.PaymentSlips.SingleAsync(
            candidate => candidate.BookingId == secondBooking.Id);
        Assert.Null(slip.SameBytesAsSlipId);
    }

    [Fact]
    public async Task A_slip_that_arrives_after_the_hold_is_refused_with_a_reason_to_act_on()
    {
        var (booker, booking) = await HeldBookingAsync();
        await scenario.LapseHoldAsync(booking.Id);

        var refused = await UploadAsync(booker, booking.Id, Jpeg());

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(SlipErrorCodes.HoldExpired, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_file_that_is_not_a_slip_is_refused_whatever_it_claims_to_be()
    {
        var (booker, booking) = await HeldBookingAsync();

        // Named and declared as a photograph, but the bytes are a script.
        var refused = await UploadAsync(
            booker, booking.Id, "<script>alert(1)</script>"u8.ToArray(), "image/jpeg", "slip.jpg");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(SlipErrorCodes.UnsupportedFile, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_file_big_enough_to_spill_to_disk_is_stored_whole()
    {
        var (booker, booking) = await HeldBookingAsync();
        // ASP.NET buffers a small form in memory and a larger one in a temp file; the upload path
        // reads the first bytes and then rewinds, so both shapes have to end up byte for byte.
        var big = new byte[256 * 1024];
        Random.Shared.NextBytes(big);
        Jpeg().CopyTo(big, 0);

        await UploadAsync(booker, booking.Id, big);

        var served = await booker.GetAsync($"/api/bookings/{booking.Id}/slip");
        Assert.Equal(big, await served.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_file_over_five_megabytes_is_refused()
    {
        var (booker, booking) = await HeldBookingAsync();
        var big = new byte[PaymentSlip.MaxBytes + 1];
        Jpeg().CopyTo(big, 0);

        var refused = await UploadAsync(booker, booking.Id, big);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(SlipErrorCodes.TooLarge, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Someone_elses_booking_is_not_theirs_to_pay_for_or_to_look_at()
    {
        var (_, booking) = await HeldBookingAsync();
        var stranger = await scenario.SignedInClientAsync();

        var upload = await UploadAsync(stranger, booking.Id, Jpeg());
        Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);

        var read = await stranger.GetAsync($"/api/bookings/{booking.Id}");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

        var slip = await stranger.GetAsync($"/api/bookings/{booking.Id}/slip");
        Assert.Equal(HttpStatusCode.NotFound, slip.StatusCode);
    }

    [Fact]
    public async Task A_booker_reads_their_own_booking_back()
    {
        var (booker, booking) = await HeldBookingAsync();

        var read = await VenueScenario.ReadAsync<BookingResponse>(
            await booker.GetAsync($"/api/bookings/{booking.Id}"));

        Assert.Equal(booking.Id, read.Id);
        Assert.Equal(nameof(BookingStatus.Held), read.Status);
        Assert.Null(read.SlipUploadedAt);
    }

    [Fact]
    public async Task A_hold_whose_time_is_up_reads_as_expired_before_anything_writes_that()
    {
        var (booker, booking) = await HeldBookingAsync();
        await scenario.LapseHoldAsync(booking.Id);

        var read = await VenueScenario.ReadAsync<BookingResponse>(
            await booker.GetAsync($"/api/bookings/{booking.Id}"));

        Assert.Equal(nameof(BookingStatus.Expired), read.Status);
    }

    [Fact]
    public async Task The_stored_bytes_are_the_bytes_that_were_sent()
    {
        var (booker, booking) = await HeldBookingAsync();
        var bytes = Png();
        await UploadAsync(booker, booking.Id, bytes);

        var served = await booker.GetAsync($"/api/bookings/{booking.Id}/slip");

        Assert.Equal(bytes, await served.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);

        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slip = await database.PaymentSlips.SingleAsync(s => s.BookingId == booking.Id);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), slip.Sha256);
        Assert.Equal(bytes.Length, slip.ByteSize);
    }

    [Fact]
    public async Task A_slip_is_handed_over_as_a_download_never_rendered_in_the_page()
    {
        var (booker, booking) = await HeldBookingAsync();
        await UploadAsync(booker, booking.Id, Pdf(), "application/pdf", "slip.pdf");

        var served = await booker.GetAsync($"/api/bookings/{booking.Id}/slip");

        // A PDF is a program as much as a document, and it came from a stranger. Rendered inline
        // it would run in the site's own origin; as a download it runs nowhere.
        Assert.Equal("attachment", served.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", served.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task One_bookers_uploads_do_not_use_up_another_bookers_allowance()
    {
        // Every test client comes from the same address, which is the point: bookers at one venue
        // share a connection, so the limit has to count people (PRD 8, Security).
        var (first, firstBooking) = await HeldBookingAsync();
        for (var attempt = 0; attempt < UploadsPerHourInTests; attempt++)
        {
            Assert.Equal(
                HttpStatusCode.OK,
                (await UploadAsync(first, firstBooking.Id, Jpeg())).StatusCode);
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await UploadAsync(first, firstBooking.Id, Jpeg())).StatusCode);

        var (second, secondBooking) = await HeldBookingAsync();
        Assert.Equal(
            HttpStatusCode.OK,
            (await UploadAsync(second, secondBooking.Id, Jpeg())).StatusCode);
    }

    [Fact]
    public async Task Every_move_a_booking_makes_is_recorded_with_who_and_when()
    {
        var (booker, booking) = await HeldBookingAsync();
        await UploadAsync(booker, booking.Id, Jpeg());
        // A second slip is not a move, so it must not add a row (PRD 6.1).
        await UploadAsync(booker, booking.Id, Png());

        var history = await scenario.HistoryAsync(booking.Id);

        Assert.Equal(2, history.Length);
        Assert.Null(history[0].From);
        Assert.Equal(BookingStatus.Held, history[0].To);
        Assert.Equal(BookingStatus.Held, history[1].From);
        Assert.Equal(BookingStatus.PendingVerification, history[1].To);
        Assert.All(history, change => Assert.NotNull(change.ChangedByUserId));
    }

    [Fact]
    public async Task A_hold_running_out_is_recorded_as_nobody_s_doing()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var booker = await scenario.SignedInClientAsync();
        var abandoned = await HoldAsync(booker, venue.Id, courts[0], 18);
        await scenario.LapseHoldAsync(abandoned.Id);

        // Booking the other court is what makes the write release the lapsed hold. Deliberately
        // not VenueScenario.ExpireLapsedHoldsAsync: that exercises the booker-scoped path, and
        // this is the venue-scoped one.
        await HoldAsync(booker, venue.Id, courts[1], 18);

        var history = await scenario.HistoryAsync(abandoned.Id);
        var expiry = Assert.Single(history, change => change.To == BookingStatus.Expired);
        Assert.Equal(BookingStatus.Held, expiry.From);
        Assert.Null(expiry.ChangedByUserId);
    }

    [Fact]
    public async Task Two_slips_at_once_record_one_move_between_them()
    {
        var (booker, booking) = await HeldBookingAsync();

        // Both requests read the booking as Held; only one of them may write the move (PRD 6.1).
        var answers = await Task.WhenAll(
            UploadAsync(booker, booking.Id, Jpeg()),
            UploadAsync(booker, booking.Id, Png()));

        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.OK, answer.StatusCode));

        var history = await scenario.HistoryAsync(booking.Id);
        Assert.Single(history, change => change.To == BookingStatus.PendingVerification);
    }

    [Fact]
    public async Task Two_requests_reaching_one_lapsed_hold_record_it_expiring_once()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync(courts: 3);
        var booker = await scenario.SignedInClientAsync();
        var abandoned = await HoldAsync(booker, venue.Id, courts[0], 18);
        await scenario.LapseHoldAsync(abandoned.Id);

        // The booker tries twice at once. Both requests clear their own lapsed hold on the way
        // past, both see it as Held, and only one of them changes it — so only one of them may
        // write that down (PRD 6.1).
        await Task.WhenAll(
            PostAsync(booker, venue.Id, courts[1], 18),
            PostAsync(booker, venue.Id, courts[2], 18));

        var history = await scenario.HistoryAsync(abandoned.Id);
        Assert.Single(history, change => change.To == BookingStatus.Expired);
    }

    [Fact]
    public async Task Sending_a_slip_needs_an_account()
    {
        var (_, booking) = await HeldBookingAsync();

        var refused = await UploadAsync(api.CreateClient(), booking.Id, Jpeg());

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    /// <summary>What ApiFactory configures, low enough that a test can reach it.</summary>
    private const int UploadsPerHourInTests = 4;

    /// <summary>The first bytes of each shape, which is all the server reads to recognise them.</summary>
    private static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, .. "JFIF payload"u8];

    private static byte[] Pdf() => [0x25, 0x50, 0x44, 0x46, .. "-1.7 slip"u8];

    private static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. "IHDR"u8];

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        Guid bookingId,
        byte[] bytes,
        string contentType = "image/jpeg",
        string fileName = "slip.jpg")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", fileName);

        return await client.PostAsync($"/api/bookings/{bookingId}/slip", form);
    }

    private async Task<(HttpClient Booker, BookingResponse Booking)> HeldBookingAsync()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        return (booker, await HoldAsync(booker, venue.Id, courts[0], 18));
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        Guid venueId,
        Guid courtId,
        int hour) =>
        client.PostAsJsonAsync(
            "/api/bookings",
            new CreateBookingRequest(
                venueId,
                [new BookingSlotRequest(courtId, VenueScenario.Today.AddDays(1), hour)]));

    private static Task<BookingResponse> HoldAsync(
        HttpClient client,
        Guid venueId,
        Guid courtId,
        int hour) =>
        VenueScenario.HoldAsync(client, venueId, VenueScenario.Today.AddDays(1), (courtId, hour));
}
