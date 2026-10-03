using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Tests.Infrastructure;

namespace CourtBooking.Api.Tests;

/// <summary>Complaints, and the one way the platform sees a slip: PRD US-22, PRD 8.</summary>
public sealed class ComplaintTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task An_admin_opens_a_complaint_about_a_booking_and_reads_its_history()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var opened = await OpenAsync(admin, booking.Id, "โอนแล้วแต่สนามไม่ยืนยัน", "Line");

        Assert.Equal("Open", opened.Status);
        Assert.Equal("Line", opened.Channel);
        Assert.Equal(ApiFactory.PlatformAdminEmail, opened.OpenedByEmail, ignoreCase: true);
        Assert.Equal(venue.Name, opened.Booking.VenueName);
        Assert.Equal("PendingVerification", opened.Booking.Status);
        Assert.True(opened.Booking.HasSlip);
        Assert.Equal(
            ["Held", "PendingVerification"],
            opened.Booking.History.Select(move => move.To));
    }

    /// <summary>
    /// A slip confirmed after the hours were played is a confirm and a complete written at the
    /// same instant. The history an admin reads must still say which came first (PRD 6.1).
    /// </summary>
    [Fact]
    public async Task Moves_made_at_the_same_instant_read_in_the_order_they_happened()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await scenario.PlayOutAsync(booking.Id);
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null)).StatusCode);

        var opened = await OpenAsync(admin, booking.Id, "เล่นไปแล้วแต่เพิ่งยืนยัน", "Email");

        Assert.Equal(
            ["Held", "PendingVerification", "Confirmed", "Completed"],
            opened.Booking.History.Select(move => move.To));
    }

    /// <summary>The rule on its own, with the tie handed over in the wrong order on purpose.</summary>
    [Fact]
    public void The_chain_decides_between_moves_that_share_a_moment()
    {
        var at = DateTimeOffset.UtcNow;
        var moves = new[]
        {
            new Move(BookingStatus.Confirmed, BookingStatus.Completed, at.AddMinutes(5)),
            new Move(BookingStatus.PendingVerification, BookingStatus.Confirmed, at.AddMinutes(5)),
            new Move(null, BookingStatus.Held, at),
            new Move(BookingStatus.Held, BookingStatus.PendingVerification, at.AddMinutes(1)),
        };

        var ordered = BookingHistory.InOrder(moves, move => move.From, move => move.To, move => move.At);

        Assert.Equal(
            [BookingStatus.Held, BookingStatus.PendingVerification, BookingStatus.Confirmed, BookingStatus.Completed],
            ordered.Select(move => move.To));
    }

    private sealed record Move(BookingStatus? From, BookingStatus To, DateTimeOffset At);

    [Theory]
    [InlineData("  ", "Email", ComplaintErrorCodes.DetailsRequired)]
    [InlineData("รายละเอียด", "1", ComplaintErrorCodes.InvalidChannel)]
    [InlineData("รายละเอียด", "Email,Phone", ComplaintErrorCodes.InvalidChannel)]
    [InlineData("รายละเอียด", null, ComplaintErrorCodes.InvalidChannel)]
    public async Task A_complaint_says_what_and_how_it_came(string details, string? channel, string code)
    {
        var admin = await scenario.PlatformAdminAsync();
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var refused = await admin.PostAsJsonAsync(
            "/api/admin/complaints", new OpenComplaintRequest(booking.Id.ToString(), details, channel));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(code, await refused.ErrorCodeAsync());
    }

    [Theory]
    [InlineData("8f2a0c7e-0000-4000-8000-000000000000")]
    [InlineData("not-a-booking-id")]
    public async Task A_complaint_about_no_booking_is_refused(string bookingId)
    {
        var admin = await scenario.PlatformAdminAsync();

        var refused = await admin.PostAsJsonAsync(
            "/api/admin/complaints", new OpenComplaintRequest(bookingId, "ไม่มีการจองนี้", "Email"));

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(ComplaintErrorCodes.BookingNotFound, await refused.ErrorCodeAsync());
    }

    /// <summary>The slip is a bank account; nobody but the platform reaches it this way (PRD 8).</summary>
    [Fact]
    public async Task Nobody_else_may_open_complaints_or_see_slips_through_them()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var opened = await OpenAsync(admin, booking.Id, "ทดสอบ", "Email");

        foreach (var client in new[] { owner, booker })
        {
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.GetAsync("/api/admin/complaints")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.GetAsync($"/api/admin/complaints/{opened.Id}/slip")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync(
                    "/api/admin/complaints",
                    new OpenComplaintRequest(booking.Id.ToString(), "ทดสอบ", "Email"))).StatusCode);
        }
    }

    /// <summary>
    /// While the complaint is open the slip can be read, and every read is written down — who,
    /// when — each time, not once (PRD US-22).
    /// </summary>
    [Fact]
    public async Task While_open_the_slip_can_be_seen_and_every_look_is_recorded()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var opened = await OpenAsync(admin, booking.Id, "ขอดูสลิป", "Phone");

        var first = await admin.GetAsync($"/api/admin/complaints/{opened.Id}/slip");
        var second = await admin.GetAsync($"/api/admin/complaints/{opened.Id}/slip");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("image/jpeg", first.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await first.Content.ReadAsByteArrayAsync());

        var after = await ReadAsync(admin, opened.Id);
        Assert.Equal(2, after.SlipViewings.Length);
        Assert.All(after.SlipViewings, viewing =>
            Assert.Equal(ApiFactory.PlatformAdminEmail, viewing.ViewedByEmail, ignoreCase: true));
    }

    /// <summary>Once resolved, the reason to look is gone, and so is the look (PRD US-22).</summary>
    [Fact]
    public async Task Once_resolved_the_slip_is_closed_to_the_platform_again()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var opened = await OpenAsync(admin, booking.Id, "ขอดูสลิป", "Email");

        var resolved = await VenueScenario.ReadAsync<ComplaintResponse>(
            await admin.PostAsJsonAsync(
                $"/api/admin/complaints/{opened.Id}/resolve",
                new ResolveComplaintRequest("สนามยืนยันแล้ว ปิดเรื่อง")));
        Assert.Equal("Resolved", resolved.Status);
        Assert.Equal("สนามยืนยันแล้ว ปิดเรื่อง", resolved.Resolution);
        Assert.Equal(ApiFactory.PlatformAdminEmail, resolved.ResolvedByEmail, ignoreCase: true);

        var refused = await admin.GetAsync($"/api/admin/complaints/{opened.Id}/slip");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(ComplaintErrorCodes.NotOpen, await refused.ErrorCodeAsync());
        Assert.Empty((await ReadAsync(admin, opened.Id)).SlipViewings);
    }

    [Fact]
    public async Task Resolving_needs_what_was_done_and_happens_once()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var opened = await OpenAsync(admin, booking.Id, "ทดสอบ", "Other");

        var empty = await admin.PostAsJsonAsync(
            $"/api/admin/complaints/{opened.Id}/resolve", new ResolveComplaintRequest(" "));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(ComplaintErrorCodes.ResolutionRequired, await empty.ErrorCodeAsync());

        var both = await Task.WhenAll(
            admin.PostAsJsonAsync(
                $"/api/admin/complaints/{opened.Id}/resolve", new ResolveComplaintRequest("หนึ่ง")),
            admin.PostAsJsonAsync(
                $"/api/admin/complaints/{opened.Id}/resolve", new ResolveComplaintRequest("สอง")));

        Assert.Single(both, response => response.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(both, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(ComplaintErrorCodes.AlreadyResolved, await loser.ErrorCodeAsync());
    }

    /// <summary>No slip, nothing to look at — and nothing is recorded as looked at.</summary>
    [Fact]
    public async Task A_booking_with_no_slip_has_nothing_to_show()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var held = await VenueScenario.HoldAsync(booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[0], 18));
        var opened = await OpenAsync(admin, held.Id, "ยังไม่ได้โอน", "Email");

        var nothing = await admin.GetAsync($"/api/admin/complaints/{opened.Id}/slip");

        Assert.Equal(HttpStatusCode.NotFound, nothing.StatusCode);
        Assert.Equal(SlipErrorCodes.NoSlip, await nothing.ErrorCodeAsync());
        Assert.False(opened.Booking.HasSlip);
        Assert.Empty((await ReadAsync(admin, opened.Id)).SlipViewings);
    }

    /// <summary>A counter customer has a name, not an account (PRD US-13).</summary>
    [Fact]
    public async Task A_counter_booking_is_named_by_its_customer()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var taken = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings",
                new CounterBookingRequest(
                    [new BookingSlotRequest(courts[0], VenueScenario.Today.AddDays(1), 18)],
                    "คุณสมชาย", "081-234-5678", "Cash")),
            HttpStatusCode.Created);

        var opened = await OpenAsync(admin, taken.BookingId, "ลูกค้าโทรมาร้องเรียน", "Phone");

        Assert.Null(opened.Booking.BookerEmail);
        Assert.Equal("คุณสมชาย", opened.Booking.CustomerName);
        Assert.Equal("Staff", opened.Booking.Channel);
    }

    [Fact]
    public async Task The_queue_lists_by_status()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var opened = await OpenAsync(admin, booking.Id, "ทดสอบ", "Email");

        var open = await VenueScenario.ReadAsync<ComplaintSummaryResponse[]>(
            await admin.GetAsync("/api/admin/complaints?status=Open"));
        var resolved = await VenueScenario.ReadAsync<ComplaintSummaryResponse[]>(
            await admin.GetAsync("/api/admin/complaints?status=Resolved"));
        var bad = await admin.GetAsync("/api/admin/complaints?status=1");

        Assert.Contains(open, one => one.Id == opened.Id);
        Assert.DoesNotContain(resolved, one => one.Id == opened.Id);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // ---------------------------------------------------------------------------------------

    private static async Task<ComplaintResponse> OpenAsync(
        HttpClient admin,
        Guid bookingId,
        string details,
        string channel) =>
        await VenueScenario.ReadAsync<ComplaintResponse>(
            await admin.PostAsJsonAsync(
                "/api/admin/complaints", new OpenComplaintRequest(bookingId.ToString(), details, channel)),
            HttpStatusCode.Created);

    private static async Task<ComplaintResponse> ReadAsync(HttpClient admin, Guid complaintId) =>
        await VenueScenario.ReadAsync<ComplaintResponse>(
            await admin.GetAsync($"/api/admin/complaints/{complaintId}"));
}
