using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>The platform deciding which venues may trade on it: PRD US-20, US-10, US-17.</summary>
public sealed class PlatformAdminTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task A_waiting_venue_can_be_approved_and_then_bookers_can_find_it()
    {
        var applicant = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(applicant);
        var admin = await scenario.PlatformAdminAsync();

        var approved = await VenueScenario.ReadAsync<AdminVenueResponse>(
            await admin.PostAsync($"/api/admin/venues/{venue.Id}/approve", null));

        Assert.Equal(nameof(VenueStatus.Approved), approved.Status);

        var anyone = api.CreateClient();
        var found = await VenueScenario.ReadAsync<PublicVenueResponse[]>(
            await anyone.GetAsync($"/api/venues/search?q={venue.Name}"));
        Assert.Contains(found, one => one.Id == venue.Id);
    }

    [Fact]
    public async Task Turning_a_venue_away_takes_a_reason_and_tells_it_why()
    {
        var (applicant, address) = await scenario.SignedInClientWithEmailAsync();
        var venue = await scenario.CreateVenueAsync(applicant);
        var admin = await scenario.PlatformAdminAsync();
        var before = api.Emails.To(address).Count;

        var noReason = await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venue.Id}/reject", new VenueDecisionRequest(null));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.Equal(VenueErrorCodes.ReasonRequired, await noReason.ErrorCodeAsync());

        var rejected = await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venue.Id}/reject",
            new VenueDecisionRequest("เลขประจำตัวผู้เสียภาษีไม่ตรงกับหนังสือรับรอง"));
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);

        // The reason is the venue's to read, so it goes to them whole (PRD US-20).
        Assert.Equal(before + 1, api.Emails.To(address).Count);
        Assert.Contains(
            "เลขประจำตัวผู้เสียภาษีไม่ตรงกับหนังสือรับรอง", api.Emails.LastTo(address).Body);
    }

    [Fact]
    public async Task A_venue_that_was_turned_away_and_asks_again_can_then_be_approved()
    {
        var applicant = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(applicant);
        var admin = await scenario.PlatformAdminAsync();

        await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venue.Id}/reject", new VenueDecisionRequest("ข้อมูลไม่ครบ"));
        await applicant.PostAsync($"/api/venues/{venue.Id}/resubmit", null);

        var approved = await admin.PostAsync($"/api/admin/venues/{venue.Id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
    }

    /// <summary>
    /// The table is what decides, so a move that is not on it is refused rather than made — the
    /// platform cannot approve a venue straight out of a suspension without lifting it, and
    /// cannot turn away one that is already trading (PRD US-20).
    /// </summary>
    [Theory]
    [InlineData("approve", "approve")]
    [InlineData("suspend", "suspend")]
    [InlineData("approve", "reject")]
    public async Task A_move_the_venue_cannot_make_is_refused(string first, string second)
    {
        var applicant = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(applicant);
        var admin = await scenario.PlatformAdminAsync();

        await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venue.Id}/{first}", new VenueDecisionRequest("เหตุผล"));

        var refused = await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venue.Id}/{second}", new VenueDecisionRequest("เหตุผล"));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(VenueErrorCodes.StatusCannotMoveThere, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Every_decision_is_written_down_with_its_reason()
    {
        var applicant = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(applicant);
        var admin = await scenario.PlatformAdminAsync();

        await admin.PostAsync($"/api/admin/venues/{venue.Id}/approve", null);
        await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venue.Id}/suspend", new VenueDecisionRequest("ร้องเรียนเรื่องเงิน"));

        var read = await VenueScenario.ReadAsync<AdminVenueDetailResponse>(
            await admin.GetAsync($"/api/admin/venues/{venue.Id}"));

        Assert.Equal(2, read.History.Length);
        Assert.Equal(nameof(VenueStatus.Approved), read.History[0].To);
        Assert.Equal(nameof(VenueStatus.Suspended), read.History[1].To);
        Assert.Equal("ร้องเรียนเรื่องเงิน", read.History[1].Reason);

        // Everything needed to judge the application is on this one answer (PRD US-20).
        Assert.False(string.IsNullOrEmpty(read.Business.TaxId));
        Assert.Equal(ApiFactory.VenueAgreementVersion, read.AgreementVersion);
    }

    // ---- What a suspension does, and what it deliberately does not ----

    [Fact]
    public async Task A_suspended_venue_disappears_from_the_bookers_side()
    {
        var (_, venue, _) = await scenario.BookableVenueAsync();
        var admin = await scenario.PlatformAdminAsync();
        await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venue.Id}/suspend", new VenueDecisionRequest("ร้องเรียน"));

        var anyone = api.CreateClient();
        var found = await VenueScenario.ReadAsync<PublicVenueResponse[]>(
            await anyone.GetAsync($"/api/venues/search?q={venue.Name}"));

        Assert.DoesNotContain(found, one => one.Id == venue.Id);
    }

    [Fact]
    public async Task And_no_new_booking_can_be_taken_there()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var admin = await scenario.PlatformAdminAsync();
        await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venue.Id}/suspend", new VenueDecisionRequest("ร้องเรียน"));

        var booker = await scenario.SignedInClientAsync();
        var refused = await booker.PostAsJsonAsync(
            "/api/bookings",
            new CreateBookingRequest(
                venue.Id,
                [new BookingSlotRequest(courts[0], VenueScenario.Today.AddDays(1), 18)]));

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    /// <summary>
    /// The rule that makes a suspension workable rather than just punitive: a slip somebody
    /// already sent is their money, sitting in a queue, and it still has to be looked at
    /// (PRD US-20).
    /// </summary>
    [Fact]
    public async Task But_slips_already_sent_can_still_be_checked()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var admin = await scenario.PlatformAdminAsync();
        await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venue.Id}/suspend", new VenueDecisionRequest("ร้องเรียน"));

        var queue = await owner.GetAsync($"/api/venues/{venue.Id}/slip-queue");
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);

        var confirmed = await owner.PostAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
    }

    /// <summary>
    /// And a booking taken before the suspension still has to be honoured or cancelled with a
    /// reason — neither of which a venue can do through a door the platform has shut (PRD US-20).
    /// </summary>
    [Fact]
    public async Task And_bookings_already_taken_can_still_be_cancelled()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        var admin = await scenario.PlatformAdminAsync();
        await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venue.Id}/suspend", new VenueDecisionRequest("ร้องเรียน"));

        var day = await owner.GetAsync(
            $"/api/venues/{venue.Id}/bookings?date={VenueScenario.Today.AddDays(1):yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, day.StatusCode);

        var cancelled = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/cancel",
            new VenueCancelRequest(nameof(CancellationReason.VenueInitiated), null, null));
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
    }

    [Fact]
    public async Task A_suspension_still_stops_the_venue_changing_what_it_sells()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var admin = await scenario.PlatformAdminAsync();
        await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venue.Id}/suspend", new VenueDecisionRequest("ร้องเรียน"));

        // Prices, courts and closures are how a venue sells, which is the thing being stopped.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/courts", new CreateCourtRequest("Court X"))).StatusCode);
    }

    [Fact]
    public async Task A_suspension_can_be_lifted_and_the_venue_is_told()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var address = (await scenario.MemberEmailsAsync(venue.Id)).Values.Single();
        var admin = await scenario.PlatformAdminAsync();
        await admin.PostAsJsonAsync(
            $"/api/admin/venues/{venue.Id}/suspend", new VenueDecisionRequest("ร้องเรียน"));

        var before = api.Emails.To(address).Count;
        var lifted = await admin.PostAsync($"/api/admin/venues/{venue.Id}/reinstate", null);

        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
        Assert.Equal(
            nameof(VenueStatus.Approved),
            (await VenueScenario.ReadAsync<AdminVenueResponse>(lifted)).Status);
        Assert.Equal(before + 1, api.Emails.To(address).Count);

        Assert.NotNull(owner);
    }

    // ---- Who may do any of this ----

    [Fact]
    public async Task Somebody_who_does_not_act_for_the_platform_cannot_judge_a_venue()
    {
        var applicant = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(applicant);

        // Not even its own owner, who holds every permission the venue has to give.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await applicant.PostAsync($"/api/admin/venues/{venue.Id}/approve", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await applicant.GetAsync("/api/admin/venues")).StatusCode);
    }

    /// <summary>
    /// The hole this closes. A configured address with no account behind it yet — a fresh
    /// deployment before its operator signs up — could be registered by anybody who guessed it,
    /// and registration takes whatever address it is given. Without the confirmation check that
    /// stranger was the platform the moment they signed in, and could read every venue's tax
    /// identity and take every venue offline (PRD US-20, security review of #27).
    /// </summary>
    [Fact]
    public async Task An_admin_address_nobody_has_proved_they_read_is_not_an_admin()
    {
        var stranger = api.CreateClient();
        var address = ApiFactory.UnclaimedAdminEmail;

        var registered = await stranger.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(
                address, "Str4ngerPassword!", ApiFactory.PrivacyPolicyVersion,
                SupportedLanguages.Thai, null));
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        // Signing in unverified is allowed on purpose (PRD US-01), so this succeeds.
        var signedIn = await stranger.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(address, "Str4ngerPassword!"));
        Assert.Equal(HttpStatusCode.NoContent, signedIn.StatusCode);

        // And that is all it gets.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await stranger.GetAsync("/api/admin/venues")).StatusCode);

        var me = await VenueScenario.ReadAsync<CurrentUserResponse>(
            await stranger.GetAsync("/api/auth/me"));
        Assert.False(me.IsPlatformAdmin);

        // Whoever can read the inbox confirms it, and only then does the address count.
        await scenario.ConfirmEmailAsync(address);
        Assert.Equal(
            HttpStatusCode.OK,
            (await stranger.GetAsync("/api/admin/venues")).StatusCode);
    }

    [Fact]
    public async Task And_neither_can_somebody_with_no_account()
    {
        var anyone = api.CreateClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anyone.GetAsync("/api/admin/venues")).StatusCode);
    }

    [Fact]
    public async Task The_platform_can_list_the_venues_that_are_waiting()
    {
        var applicant = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(applicant);
        var admin = await scenario.PlatformAdminAsync();

        var waiting = await VenueScenario.ReadAsync<AdminVenueResponse[]>(
            await admin.GetAsync($"/api/admin/venues?status={nameof(VenueStatus.Pending)}"));

        Assert.Contains(waiting, one => one.Id == venue.Id);
        Assert.All(waiting, one => Assert.Equal(nameof(VenueStatus.Pending), one.Status));
    }

    [Theory]
    [InlineData("Nonsense")]
    [InlineData("2")]
    public async Task A_status_that_is_not_one_is_refused(string status)
    {
        var admin = await scenario.PlatformAdminAsync();

        var refused = await admin.GetAsync($"/api/admin/venues?status={status}");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(VenueErrorCodes.InvalidStatus, await refused.ErrorCodeAsync());
    }
}
