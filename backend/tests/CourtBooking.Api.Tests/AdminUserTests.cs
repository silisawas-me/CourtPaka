using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace CourtBooking.Api.Tests;

/// <summary>The platform finding and stopping accounts: PRD US-22.</summary>
public sealed class AdminUserTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task An_admin_finds_somebody_by_part_of_their_address()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (_, email) = await scenario.SignedInClientWithEmailAsync();
        var part = email[..email.IndexOf('@')].ToUpperInvariant();

        var found = await VenueScenario.ReadAsync<AdminUserResponse[]>(
            await admin.GetAsync($"/api/admin/users?q={part}"));

        var one = Assert.Single(found);
        Assert.Equal(email, one.Email, ignoreCase: true);
        Assert.Null(one.SuspendedAt);
    }

    /// <summary>For finding somebody, not for paging through everybody (PDPA, PRD 8).</summary>
    [Fact]
    public async Task A_search_too_short_to_find_somebody_is_refused()
    {
        var admin = await scenario.PlatformAdminAsync();

        var refused = await admin.GetAsync("/api/admin/users?q=ab");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(AdminUserErrorCodes.QueryTooShort, await refused.ErrorCodeAsync());
    }

    /// <summary>
    /// "%" and "_" are characters to find, not patterns to widen the search with — and an
    /// address that really has one is still found by it.
    /// </summary>
    [Theory]
    [InlineData("%25%25%25")]
    [InlineData("___")]
    public async Task A_wildcard_in_the_search_is_only_a_character(string query)
    {
        var admin = await scenario.PlatformAdminAsync();
        await scenario.SignedInClientAsync();

        var found = await VenueScenario.ReadAsync<AdminUserResponse[]>(
            await admin.GetAsync($"/api/admin/users?q={query}"));

        Assert.Empty(found);
    }

    [Fact]
    public async Task An_address_with_an_underscore_is_found_by_it()
    {
        var admin = await scenario.PlatformAdminAsync();
        var email = $"under_{Guid.NewGuid():N}@example.com";
        await RegisterAsync(api.CreateClient(), email);

        var found = await VenueScenario.ReadAsync<AdminUserResponse[]>(
            await admin.GetAsync($"/api/admin/users?q={email[..10]}"));

        Assert.Contains(found, one => one.Email == email);
    }

    [Fact]
    public async Task Nobody_else_may_look()
    {
        var booker = await scenario.SignedInClientAsync();

        var refused = await booker.GetAsync("/api/admin/users?q=example");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task A_suspension_needs_a_reason()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (userId, _) = await SomebodyAsync(admin);

        var refused = await admin.PostAsJsonAsync(
            $"/api/admin/users/{userId}/suspend", new AccountStandingRequest("  "));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(AdminUserErrorCodes.ReasonRequired, await refused.ErrorCodeAsync());
    }

    /// <summary>
    /// Suspended, they cannot sign in — and they are told why only once they have shown they know
    /// the password, so nobody else learns the account exists (PDPA, PRD 8).
    /// </summary>
    [Fact]
    public async Task A_suspended_account_cannot_sign_in()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (userId, email) = await SomebodyAsync(admin);

        await SuspendAsync(admin, userId, "โกงการจองซ้ำหลายครั้ง");

        var right = await api.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(email, VenueScenario.Password));
        Assert.Equal(HttpStatusCode.Forbidden, right.StatusCode);
        Assert.Equal(AuthErrorCodes.AccountSuspended, await right.ErrorCodeAsync());

        var wrong = await api.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(email, "NotThePassword1"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(AuthErrorCodes.InvalidCredentials, await wrong.ErrorCodeAsync());
    }

    /// <summary>
    /// Wrong passwords on a suspended account still count towards the lockout. Without that,
    /// the suspended answer would be a way to try passwords forever (PRD US-01).
    /// </summary>
    [Fact]
    public async Task Guessing_at_a_suspended_account_still_locks_it()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (userId, email) = await SomebodyAsync(admin);
        await SuspendAsync(admin, userId, "ทดสอบ");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await api.CreateClient().PostAsJsonAsync(
                "/api/auth/login", new LoginRequest(email, "NotThePassword1"));
        }

        var right = await api.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(email, VenueScenario.Password));

        // Locked, the right password earns nothing more than a wrong one would.
        Assert.Equal(HttpStatusCode.Unauthorized, right.StatusCode);
        Assert.Equal(AuthErrorCodes.InvalidCredentials, await right.ErrorCodeAsync());
    }

    /// <summary>
    /// Between the suspension and the next revalidation an open session still works; holding a
    /// court is refused in that window anyway, because booking reads the account row (US-22).
    /// </summary>
    [Fact]
    public async Task A_session_not_yet_revalidated_still_cannot_book()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (_, venue, courts) = await scenario.BookableVenueAsync();

        // A host that revalidates sessions rarely, as production does.
        using var slow = api.Api.WithWebHostBuilder(builder =>
            builder.UseSetting("App:SessionRevalidationSeconds", "3600"));
        var session = slow.CreateClient();
        var email = scenario.NewEmail();
        await RegisterAsync(session, email);
        await scenario.ConfirmEmailAsync(email);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await session.PostAsJsonAsync(
                "/api/auth/login", new LoginRequest(email, VenueScenario.Password))).StatusCode);

        await SuspendAsync(admin, await IdOfAsync(admin, email), "โกงการจอง");

        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/auth/me")).StatusCode);
        var refused = await session.PostAsJsonAsync(
            "/api/bookings",
            new CreateBookingRequest(
                venue.Id, [new BookingSlotRequest(courts[0], VenueScenario.Today.AddDays(1), 18)]));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(AuthErrorCodes.AccountSuspended, await refused.ErrorCodeAsync());
    }

    /// <summary>A session already open ends too, at the next revalidation (0s in tests).</summary>
    [Fact]
    public async Task And_a_session_already_open_ends()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (session, email) = await scenario.SignedInClientWithEmailAsync();
        var userId = await IdOfAsync(admin, email);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/auth/me")).StatusCode);

        await SuspendAsync(admin, userId, "บัญชีถูกใช้โดยผู้อื่น");

        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/auth/me")).StatusCode);
    }

    /// <summary>A booking already confirmed is the venue's business with a customer (PRD US-22).</summary>
    [Fact]
    public async Task What_they_already_booked_stays_booked()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);
        var me = await VenueScenario.ReadAsync<CurrentUserResponse>(await booker.GetAsync("/api/auth/me"));

        await SuspendAsync(admin, me.Id, "ทดสอบ");

        Assert.Equal(BookingStatus.Confirmed, (await scenario.StoredBookingAsync(booking.Id)).Status);
    }

    [Fact]
    public async Task Reinstated_they_can_sign_in_again_and_the_history_says_both()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (userId, email) = await SomebodyAsync(admin);
        await SuspendAsync(admin, userId, "ร้องเรียนซ้ำ");

        var back = await VenueScenario.ReadAsync<AdminUserDetailResponse>(
            await admin.PostAsJsonAsync(
                $"/api/admin/users/{userId}/reinstate", new AccountStandingRequest("ตรวจแล้วไม่ผิด")));

        Assert.Null(back.User.SuspendedAt);
        Assert.Equal([false, true], back.History.Select(change => change.Suspended));
        Assert.Equal(ApiFactory.PlatformAdminEmail, back.History[0].ChangedByEmail, ignoreCase: true);

        var login = await api.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(email, VenueScenario.Password));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    public async Task Suspending_twice_is_refused()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (userId, _) = await SomebodyAsync(admin);
        await SuspendAsync(admin, userId, "ครั้งแรก");

        var again = await admin.PostAsJsonAsync(
            $"/api/admin/users/{userId}/suspend", new AccountStandingRequest("ครั้งที่สอง"));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(AdminUserErrorCodes.AlreadySuspended, await again.ErrorCodeAsync());
    }

    /// <summary>Being an admin is configuration; the app does not take it away (PRD US-20).</summary>
    [Fact]
    public async Task A_platform_admin_cannot_be_suspended()
    {
        var admin = await scenario.PlatformAdminAsync();
        var self = await VenueScenario.ReadAsync<CurrentUserResponse>(await admin.GetAsync("/api/auth/me"));

        var refused = await admin.PostAsJsonAsync(
            $"/api/admin/users/{self.Id}/suspend", new AccountStandingRequest("ลองดู"));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(AdminUserErrorCodes.CannotSuspendAdmin, await refused.ErrorCodeAsync());
    }

    // ---------------------------------------------------------------------------------------

    private async Task<(Guid UserId, string Email)> SomebodyAsync(HttpClient admin)
    {
        var (_, email) = await scenario.SignedInClientWithEmailAsync();
        return (await IdOfAsync(admin, email), email);
    }

    private static async Task<Guid> IdOfAsync(HttpClient admin, string email)
    {
        var found = await VenueScenario.ReadAsync<AdminUserResponse[]>(
            await admin.GetAsync($"/api/admin/users?q={Uri.EscapeDataString(email)}"));
        return Assert.Single(found).Id;
    }

    private static async Task RegisterAsync(HttpClient client, string email)
    {
        var registered = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(
                email, VenueScenario.Password, ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, null));
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
    }

    private static async Task SuspendAsync(HttpClient admin, Guid userId, string reason)
    {
        var suspended = await admin.PostAsJsonAsync(
            $"/api/admin/users/{userId}/suspend", new AccountStandingRequest(reason));
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
    }
}
