using System.Net;
using System.Net.Http.Json;
using System.Web;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>
/// Signing in with LINE (PRD US-01): an account made on the platform's own consent step, a way
/// back in afterwards, and the rules that hold whichever way somebody got in.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LineLoginTests(ApiTestFixture api)
{
    private readonly VenueScenario scenario = new(api);

    private static string NewSubject() => $"U{Guid.NewGuid():N}";

    /// <summary>A browser: it keeps cookies and does not follow redirects by itself.</summary>
    private HttpClient Browser() =>
        api.Api.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// Goes to LINE and comes back with the code, the way the browser does. Proving again who you
    /// are is a POST, which only a page of this site can make the browser send (see the endpoint).
    /// </summary>
    private async Task<HttpResponseMessage> SignInWithLineAsync(
        HttpClient browser, string code, string? returnUrl = null, string? purpose = null)
    {
        var url = $"/api/auth/line/start?returnUrl={HttpUtility.UrlEncode(returnUrl ?? "/")}"
            + (purpose is null ? "" : $"&purpose={purpose}");
        var start = purpose == "confirm"
            ? await browser.PostAsync(url, null)
            : await browser.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);

        var state = StateOf(start);
        return await browser.GetAsync($"/api/auth/line/callback?code={code}&state={HttpUtility.UrlEncode(state)}");
    }

    private static string StateOf(HttpResponseMessage start) =>
        HttpUtility.ParseQueryString(new Uri(start.Headers.Location!.ToString()).Query)["state"]!;

    private static string WhereTo(HttpResponseMessage response) =>
        response.Headers.Location!.ToString();

    private static async Task<CurrentUserResponse> MeAsync(HttpClient client) =>
        await VenueScenario.ReadAsync<CurrentUserResponse>(await client.GetAsync("/api/auth/me"));

    [Fact]
    public async Task A_first_sign_in_becomes_an_account_only_once_the_policy_is_accepted()
    {
        var browser = Browser();
        var subject = NewSubject();
        var email = $"line-{Guid.NewGuid():N}@example.com";

        var back = await SignInWithLineAsync(browser, api.Line.Grant(subject, "ปกป้อง", email), "/book");
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.StartsWith("/register/line", WhereTo(back), StringComparison.Ordinal);

        // LINE's own consent screen is not consent to this platform's policy, so no account yet.
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);

        var waiting = await VenueScenario.ReadAsync<LinePendingResponse>(
            await browser.GetAsync("/api/auth/line/pending"));
        Assert.Equal(("ปกป้อง", email), (waiting.Name, waiting.Email));

        var completed = await browser.PostAsJsonAsync(
            "/api/auth/line/complete",
            new CompleteLineSignUpRequest(ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, "081-234-5678"));
        Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);

        var me = await MeAsync(browser);
        Assert.Equal(email, me.Email);
        Assert.False(me.EmailConfirmed);
        Assert.True(me.SignsInWithLine);
        Assert.False(me.HasPassword);
        // Typed with dashes, kept as a venue would dial it.
        Assert.Equal("0812345678", me.PhoneNumber);
        Assert.Null(me.CannotBookBecause);

        // The address came from LINE, so it is verified here the platform's own way before
        // anything is sent to it.
        Assert.Contains(api.Emails.To(email), sent => sent.Template == AccountLetters.VerifyTemplate);

        // And the consent is on the record, with the version they accepted (PDPA, PRD 8).
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var consent = await database.UserConsents.SingleAsync(row => row.UserId == me.Id);
        Assert.Equal(ApiFactory.PrivacyPolicyVersion, consent.Version);
    }

    [Fact]
    public async Task A_second_sign_in_goes_straight_in_and_back_to_where_it_started()
    {
        var subject = NewSubject();
        var first = Browser();
        await SignInWithLineAsync(first, api.Line.Grant(subject));
        await first.PostAsJsonAsync(
            "/api/auth/line/complete",
            new CompleteLineSignUpRequest(ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, "0812345678"));
        var id = (await MeAsync(first)).Id;

        var later = Browser();
        var back = await SignInWithLineAsync(later, api.Line.Grant(subject), "/bookings");

        Assert.Equal("/bookings", WhereTo(back));
        Assert.Equal(id, (await MeAsync(later)).Id);
    }

    /// <summary>
    /// The state is what ties LINE's answer to the attempt this browser started. Without the
    /// check, a link could sign a victim's browser into the attacker's LINE account.
    /// </summary>
    [Fact]
    public async Task An_answer_this_browser_did_not_ask_for_is_refused()
    {
        var browser = Browser();
        var code = api.Line.Grant(NewSubject());
        await browser.GetAsync("/api/auth/line/start?returnUrl=/");

        var forged = await browser.GetAsync($"/api/auth/line/callback?code={code}&state=somebody-elses-state");

        Assert.Equal($"/login?line={LineErrorCodes.Failed}", WhereTo(forged));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/api/auth/line/pending")).StatusCode);
    }

    [Fact]
    public async Task A_code_cannot_be_used_twice()
    {
        var browser = Browser();
        var code = api.Line.Grant(NewSubject());
        var first = await SignInWithLineAsync(browser, code);
        Assert.StartsWith("/register/line", WhereTo(first), StringComparison.Ordinal);

        var again = await SignInWithLineAsync(Browser(), code);

        Assert.Equal($"/login?line={LineErrorCodes.Failed}", WhereTo(again));
    }

    [Fact]
    public async Task Saying_no_on_LINE_says_so_and_nothing_else()
    {
        var browser = Browser();
        var start = await browser.GetAsync("/api/auth/line/start?returnUrl=/");

        var refused = await browser.GetAsync(
            $"/api/auth/line/callback?error=access_denied&state={HttpUtility.UrlEncode(StateOf(start))}");

        Assert.Equal($"/login?line={LineErrorCodes.Denied}", WhereTo(refused));
    }

    /// <summary>An address is where a message goes, never a way into an account that has it.</summary>
    [Fact]
    public async Task An_address_another_account_already_has_is_not_taken_over()
    {
        var (_, existing) = await scenario.SignedInClientWithEmailAsync();

        var browser = Browser();
        await SignInWithLineAsync(browser, api.Line.Grant(NewSubject(), "ปกป้อง", existing));
        await browser.PostAsJsonAsync(
            "/api/auth/line/complete",
            new CompleteLineSignUpRequest(ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, "0812345678"));

        var me = await MeAsync(browser);
        Assert.Null(me.Email);
        // The account that owns the address is untouched, and nothing was sent to it.
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await database.Users.SingleAsync(user => user.Email == existing);
        Assert.NotEqual(me.Id, owner.Id);
        // Only the one its own registration sent: the LINE sign-up wrote to nobody.
        Assert.Single(api.Emails.To(existing), sent => sent.Template == AccountLetters.VerifyTemplate);
    }

    /// <summary>
    /// A LINE account may have no proved address, so the phone number is what a venue reaches the
    /// booker on — and until there is one, there is no booking (PRD US-01).
    /// </summary>
    [Fact]
    public async Task A_LINE_account_books_once_it_has_given_a_phone_number()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var browser = Browser();
        await SignInWithLineAsync(browser, api.Line.Grant(NewSubject()));
        await browser.PostAsJsonAsync(
            "/api/auth/line/complete",
            new CompleteLineSignUpRequest(ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, null));

        Assert.Equal(AuthErrorCodes.PhoneRequired, (await MeAsync(browser)).CannotBookBecause);
        var refused = await browser.PostAsJsonAsync(
            "/api/bookings", Hold(venue.Id, courts[0], 18));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(AuthErrorCodes.PhoneRequired, await refused.ErrorCodeAsync());

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await browser.PutAsJsonAsync("/api/auth/me/phone", new ChangePhoneRequest("+66812345678"))).StatusCode);

        Assert.Null((await MeAsync(browser)).CannotBookBecause);
        var held = await browser.PostAsJsonAsync("/api/bookings", Hold(venue.Id, courts[0], 18));
        Assert.Equal(HttpStatusCode.Created, held.StatusCode);
        // +66 is the same number as the one a venue would dial.
        Assert.Equal("0812345678", (await MeAsync(browser)).PhoneNumber);
    }

    [Fact]
    public async Task A_number_that_is_not_a_phone_number_is_refused()
    {
        var browser = Browser();
        await SignInWithLineAsync(browser, api.Line.Grant(NewSubject()));
        await browser.PostAsJsonAsync(
            "/api/auth/line/complete",
            new CompleteLineSignUpRequest(ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, null));

        var refused = await browser.PutAsJsonAsync("/api/auth/me/phone", new ChangePhoneRequest("12345"));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(AuthErrorCodes.InvalidPhone, await refused.ErrorCodeAsync());
    }

    /// <summary>Everything a venue is told is an email, so its owner has to have one (PRD US-17).</summary>
    [Fact]
    public async Task A_LINE_account_with_no_address_cannot_run_a_venue()
    {
        var browser = Browser();
        await SignInWithLineAsync(browser, api.Line.Grant(NewSubject()));
        await browser.PostAsJsonAsync(
            "/api/auth/line/complete",
            new CompleteLineSignUpRequest(ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, "0812345678"));

        var refused = await browser.PostAsJsonAsync(
            "/api/venues", VenueScenario.Application(scenario.NewCode()));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(VenueErrorCodes.OwnerNeedsEmail, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_suspended_account_does_not_get_in_through_LINE_either()
    {
        var subject = NewSubject();
        var browser = Browser();
        await SignInWithLineAsync(browser, api.Line.Grant(subject));
        await browser.PostAsJsonAsync(
            "/api/auth/line/complete",
            new CompleteLineSignUpRequest(ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, "0812345678"));
        var id = (await MeAsync(browser)).Id;

        var admin = await scenario.PlatformAdminAsync();
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PostAsJsonAsync(
                $"/api/admin/users/{id}/suspend", new AccountStandingRequest("ทดสอบ"))).StatusCode);

        var back = await SignInWithLineAsync(Browser(), api.Line.Grant(subject));

        Assert.Equal($"/login?line={AuthErrorCodes.AccountSuspended}", WhereTo(back));
    }

    /// <summary>
    /// There is no password to ask for again, so the person goes back to LINE instead — and what
    /// is deleted includes the LINE id, or signing in again would walk back into the account
    /// (PDPA, S-15).
    /// </summary>
    [Fact]
    public async Task Deleting_a_LINE_account_is_confirmed_at_LINE_and_forgets_the_LINE_id()
    {
        var subject = NewSubject();
        var browser = Browser();
        await SignInWithLineAsync(browser, api.Line.Grant(subject));
        await browser.PostAsJsonAsync(
            "/api/auth/line/complete",
            new CompleteLineSignUpRequest(ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, "0812345678"));
        var id = (await MeAsync(browser)).Id;

        var tooSoon = await browser.PostAsJsonAsync("/api/auth/me/delete", new DeleteAccountRequest(null));
        Assert.Equal(HttpStatusCode.Forbidden, tooSoon.StatusCode);
        Assert.Equal(AccountDeletionErrorCodes.ConfirmWithLine, await tooSoon.ErrorCodeAsync());

        var confirmed = await SignInWithLineAsync(browser, api.Line.Grant(subject), purpose: "confirm");
        Assert.Equal("/account?line=confirmed", WhereTo(confirmed));

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await browser.PostAsJsonAsync("/api/auth/me/delete", new DeleteAccountRequest(null))).StatusCode);

        // Signing in with the same LINE account afterwards is a new person, not this one.
        var stranger = Browser();
        var afterwards = await SignInWithLineAsync(stranger, api.Line.Grant(subject));
        Assert.StartsWith("/register/line", WhereTo(afterwards), StringComparison.Ordinal);

        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await database.UserLogins.AnyAsync(login => login.UserId == id));
        Assert.Equal(
            AccountDeletion.Placeholder(id),
            await database.Users.Where(user => user.Id == id).Select(user => user.Email).SingleAsync());
    }

    /// <summary>Somebody else's LINE account confirms nothing about this one.</summary>
    [Fact]
    public async Task Another_LINE_account_cannot_confirm_this_one()
    {
        var browser = Browser();
        await SignInWithLineAsync(browser, api.Line.Grant(NewSubject()));
        await browser.PostAsJsonAsync(
            "/api/auth/line/complete",
            new CompleteLineSignUpRequest(ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, "0812345678"));

        var elsewhere = await SignInWithLineAsync(browser, api.Line.Grant(NewSubject()), purpose: "confirm");

        Assert.Equal($"/account?line={LineErrorCodes.Mismatch}", WhereTo(elsewhere));
        var refused = await browser.PostAsJsonAsync("/api/auth/me/delete", new DeleteAccountRequest(null));
        Assert.Equal(AccountDeletionErrorCodes.ConfirmWithLine, await refused.ErrorCodeAsync());
    }

    /// <summary>
    /// A venue has to be able to reach whoever booked. A LINE account may have no address, so the
    /// number it gave stands in its place — in the slip queue and in the day's list (PRD US-01).
    /// </summary>
    [Fact]
    public async Task A_venue_reaches_a_LINE_booker_who_has_no_address_on_their_phone()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var browser = Browser();
        await SignInWithLineAsync(browser, api.Line.Grant(NewSubject()));
        await browser.PostAsJsonAsync(
            "/api/auth/line/complete",
            new CompleteLineSignUpRequest(ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, "0899999999"));

        var held = await VenueScenario.ReadAsync<BookingResponse>(
            await browser.PostAsJsonAsync("/api/bookings", Hold(venue.Id, courts[0], 20)),
            HttpStatusCode.Created);
        Assert.Equal(
            HttpStatusCode.OK,
            (await VenueScenario.UploadAsync(browser, held.Id, VenueScenario.Jpeg())).StatusCode);

        var queue = await VenueScenario.ReadAsync<SlipQueueItemResponse[]>(
            await owner.GetAsync($"/api/venues/{venue.Id}/slip-queue"));
        var waiting = Assert.Single(queue, item => item.BookingId == held.Id);
        Assert.Null(waiting.BookerEmail);
        Assert.Equal("0899999999", waiting.BookerPhone);

        var day = await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await owner.GetAsync(
                $"/api/venues/{venue.Id}/bookings?date={VenueScenario.Today.AddDays(1):yyyy-MM-dd}"));
        var row = Assert.Single(day, booking => booking.BookingId == held.Id);
        Assert.Null(row.BookerEmail);
        Assert.Equal("0899999999", row.BookerPhone);
    }

    /// <summary>
    /// The step-up is a POST for a reason: a GET could be followed from anybody's link, and would
    /// hand out five minutes of "I proved who I am" that the person never meant to give.
    /// </summary>
    [Fact]
    public async Task Following_a_link_cannot_prove_who_you_are()
    {
        var subject = NewSubject();
        var browser = Browser();
        await SignInWithLineAsync(browser, api.Line.Grant(subject));
        await browser.PostAsJsonAsync(
            "/api/auth/line/complete",
            new CompleteLineSignUpRequest(ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, "0812345678"));

        // A link is a GET: it starts an ordinary sign-in, which confirms nothing.
        var followed = await SignInWithLineAsync(browser, api.Line.Grant(subject), "/account");
        Assert.Equal("/account", WhereTo(followed));

        var refused = await browser.PostAsJsonAsync("/api/auth/me/delete", new DeleteAccountRequest(null));
        Assert.Equal(AccountDeletionErrorCodes.ConfirmWithLine, await refused.ErrorCodeAsync());
    }

    /// <summary>The policy version comes from the server, and a stale one is refused (PDPA).</summary>
    [Fact]
    public async Task A_sign_up_that_accepted_an_old_policy_is_refused()
    {
        var browser = Browser();
        await SignInWithLineAsync(browser, api.Line.Grant(NewSubject()));

        var refused = await browser.PostAsJsonAsync(
            "/api/auth/line/complete",
            new CompleteLineSignUpRequest("1999-01-01", SupportedLanguages.Thai, "0812345678"));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(AuthErrorCodes.PrivacyPolicyOutdated, await refused.ErrorCodeAsync());
        // Still waiting, so accepting the current version still works.
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await browser.PostAsJsonAsync(
                "/api/auth/line/complete",
                new CompleteLineSignUpRequest(
                    ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, "0812345678"))).StatusCode);
    }

    /// <summary>Finishing without LINE having said anything is nothing to finish.</summary>
    [Fact]
    public async Task Finishing_a_sign_up_nobody_started_is_refused()
    {
        var refused = await Browser().PostAsJsonAsync(
            "/api/auth/line/complete",
            new CompleteLineSignUpRequest(ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, null));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(LineErrorCodes.Expired, await refused.ErrorCodeAsync());
    }

    /// <summary>An address LINE hands back that is not one is dropped, not a dead end.</summary>
    [Fact]
    public async Task An_address_that_is_not_an_address_is_left_behind()
    {
        var browser = Browser();
        await SignInWithLineAsync(browser, api.Line.Grant(NewSubject(), "ปกป้อง", "not-an-address"));

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await browser.PostAsJsonAsync(
                "/api/auth/line/complete",
                new CompleteLineSignUpRequest(
                    ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, "0812345678"))).StatusCode);
        Assert.Null((await MeAsync(browser)).Email);
    }

    /// <summary>
    /// An account an admin cannot find is an account they cannot suspend (PRD US-22). A LINE
    /// account may have no address, so the number it gave is what it is found by.
    /// </summary>
    [Fact]
    public async Task A_LINE_account_with_no_address_is_found_by_its_phone_number()
    {
        var phone = $"09{Random.Shared.Next(10_000_000, 99_999_999)}";
        var browser = Browser();
        await SignInWithLineAsync(browser, api.Line.Grant(NewSubject()));
        await browser.PostAsJsonAsync(
            "/api/auth/line/complete",
            new CompleteLineSignUpRequest(ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, phone));
        var id = (await MeAsync(browser)).Id;

        var admin = await scenario.PlatformAdminAsync();
        var found = await VenueScenario.ReadAsync<AdminUserResponse[]>(
            await admin.GetAsync($"/api/admin/users?q={phone}"));

        var row = Assert.Single(found, user => user.Id == id);
        Assert.Null(row.Email);
        Assert.Equal(phone, row.PhoneNumber);
    }

    private static CreateBookingRequest Hold(Guid venueId, Guid courtId, int hour) =>
        new(venueId, [new BookingSlotRequest(courtId, VenueScenario.Today.AddDays(1), hour)]);
}
