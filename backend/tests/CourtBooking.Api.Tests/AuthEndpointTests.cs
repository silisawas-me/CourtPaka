using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class AuthEndpointTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string DefaultPassword = "CorrectHorse1";

    private readonly AuthApiFixture _api = new(postgres);

    public Task InitializeAsync() => _api.InitializeAsync();

    public Task DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task The_privacy_policy_version_comes_from_the_server()
    {
        using var client = _api.CreateClient();

        var policy = await client.GetFromJsonAsync<PrivacyPolicyResponse>("/api/auth/privacy-policy");

        Assert.Equal(ApiFactory.PrivacyPolicyVersion, policy!.Version);
    }

    [Fact]
    public async Task Registration_creates_an_unverified_account_and_emails_a_verification_link()
    {
        using var client = _api.CreateClient();

        var email = await RegisterAsync(client);

        Assert.Contains("verify", _api.Emails.LastTo(email).Subject, StringComparison.OrdinalIgnoreCase);
        await LoginAsync(client, email);
        var me = await GetCurrentUserAsync(client);
        Assert.False(me.EmailConfirmed);
        Assert.Equal(SupportedLanguages.Thai, me.Language);
    }

    [Fact]
    public async Task Registration_records_the_accepted_privacy_policy()
    {
        using var client = _api.CreateClient();
        var email = await RegisterAsync(client);

        var consents = await ReadConsentsAsync(email);

        var consent = Assert.Single(consents);
        Assert.Equal(ConsentType.PrivacyPolicy, consent.Type);
        Assert.Equal(ApiFactory.PrivacyPolicyVersion, consent.Version);
    }

    [Fact]
    public async Task Registration_is_refused_when_the_accepted_policy_is_not_the_current_one()
    {
        using var client = _api.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register", NewRegistration(NewEmail()) with { PrivacyPolicyVersion = "2020-01-01" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(AuthErrorCodes.PrivacyPolicyOutdated, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Verifying_the_emailed_token_confirms_the_account()
    {
        using var client = _api.CreateClient();
        var email = await RegisterAsync(client);
        var (userId, token) = ReadVerificationLink(_api.Emails.LastTo(email).Body);

        var response = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(userId, token));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await LoginAsync(client, email);
        Assert.True((await GetCurrentUserAsync(client)).EmailConfirmed);
    }

    [Fact]
    public async Task A_session_started_before_verification_sees_the_account_as_verified_afterwards()
    {
        using var client = _api.CreateClient();
        var email = await RegisterAsync(client);
        await LoginAsync(client, email);
        Assert.False((await GetCurrentUserAsync(client)).EmailConfirmed);

        var (userId, token) = ReadVerificationLink(_api.Emails.LastTo(email).Body);
        await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(userId, token));

        Assert.True((await GetCurrentUserAsync(client)).EmailConfirmed);
    }

    [Fact]
    public async Task A_tampered_verification_token_is_rejected()
    {
        using var client = _api.CreateClient();
        var email = await RegisterAsync(client);
        var (userId, token) = ReadVerificationLink(_api.Emails.LastTo(email).Body);

        var response = await client.PostAsJsonAsync(
            "/api/auth/verify-email", new VerifyEmailRequest(userId, token[..^4] + "AAAA"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AuthErrorCodes.InvalidVerificationToken, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Registering_an_address_that_already_exists_does_not_reveal_it()
    {
        using var client = _api.CreateClient();
        var email = await RegisterAsync(client);

        var response = await client.PostAsJsonAsync("/api/auth/register", NewRegistration(email, "An0therPass!"));

        // Same status as a fresh registration; the address owner is told by email instead.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("already exists", _api.Emails.LastTo(email).Subject, StringComparison.OrdinalIgnoreCase);

        // The original password still works, so the second attempt did not touch the account.
        await LoginAsync(client, email);
        Assert.Single(await ReadConsentsAsync(email));
    }

    [Theory]
    [InlineData("short1")]
    [InlineData("alllowercase")]
    public async Task Passwords_that_break_the_configured_rules_are_rejected(string password)
    {
        using var client = _api.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", NewRegistration(NewEmail(), password));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AuthErrorCodes.WeakPassword, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_address_Identity_rejects_answers_with_the_email_code()
    {
        using var client = _api.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", NewRegistration("not-an-email"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AuthErrorCodes.InvalidEmail, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_unsupported_language_is_rejected()
    {
        using var client = _api.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register", NewRegistration(NewEmail()) with { Language = "fr" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AuthErrorCodes.UnsupportedLanguage, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task The_configured_number_of_failed_sign_ins_locks_the_account()
    {
        var lockout = _api.GetService<IOptions<IdentityOptions>>().Value.Lockout;
        using var client = _api.CreateClient();
        var email = await RegisterAsync(client);

        for (var attempt = 1; attempt <= lockout.MaxFailedAccessAttempts; attempt++)
        {
            var failure = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "WrongPassword1"));
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        // Now locked: the right password no longer works either.
        var lockedOut = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, DefaultPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, lockedOut.StatusCode);
        Assert.Equal(AuthErrorCodes.InvalidCredentials, await ReadErrorCodeAsync(lockedOut));
        Assert.Contains("locked", _api.Emails.LastTo(email).Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_locked_account_answers_exactly_like_an_address_with_no_account()
    {
        var lockout = _api.GetService<IOptions<IdentityOptions>>().Value.Lockout;
        using var client = _api.CreateClient();
        var registered = await RegisterAsync(client);

        for (var attempt = 1; attempt <= lockout.MaxFailedAccessAttempts; attempt++)
        {
            await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(registered, "WrongPassword1"));
        }

        var lockedResponse = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(registered, "WrongPassword1"));
        var unknownResponse = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(NewEmail(), "WrongPassword1"));

        // Same status and same code, so the endpoint cannot be used to find out who has an account.
        Assert.Equal(unknownResponse.StatusCode, lockedResponse.StatusCode);
        Assert.Equal(await ReadErrorCodeAsync(unknownResponse), await ReadErrorCodeAsync(lockedResponse));
    }

    [Fact]
    public async Task The_chosen_language_is_stored_and_can_be_changed()
    {
        using var client = _api.CreateClient();
        var email = await RegisterAsync(client, SupportedLanguages.English);
        await LoginAsync(client, email);

        Assert.Equal(SupportedLanguages.English, (await GetCurrentUserAsync(client)).Language);

        var changed = await client.PutAsJsonAsync(
            "/api/auth/me/language", new ChangeLanguageRequest(SupportedLanguages.Thai));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(SupportedLanguages.Thai, (await GetCurrentUserAsync(client)).Language);

        var rejected = await client.PutAsJsonAsync("/api/auth/me/language", new ChangeLanguageRequest("fr"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task Verification_email_is_sent_in_the_language_chosen_at_registration()
    {
        using var client = _api.CreateClient();

        var email = await RegisterAsync(client, SupportedLanguages.English);

        Assert.Equal(SupportedLanguages.English, _api.Emails.LastTo(email).Language);
    }

    [Fact]
    public async Task A_verification_email_can_be_sent_again()
    {
        using var client = _api.CreateClient();
        var email = await RegisterAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/auth/resend-verification", new ResendVerificationRequest(email));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var (userId, token) = ReadVerificationLink(_api.Emails.LastTo(email).Body);
        var verified = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(userId, token));
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);
    }

    [Fact]
    public async Task Resending_to_an_unknown_address_answers_the_same_way()
    {
        using var client = _api.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/resend-verification", new ResendVerificationRequest(NewEmail()));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Revoking_the_security_stamp_ends_existing_sessions()
    {
        using var client = _api.CreateClient();
        var email = await RegisterAsync(client);
        await LoginAsync(client, email);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        using (var scope = _api.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await users.UpdateSecurityStampAsync((await users.FindByEmailAsync(email))!);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        using var client = _api.CreateClient();
        var email = await RegisterAsync(client);
        await LoginAsync(client, email);

        var loggedOut = await client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_callers_cannot_read_the_current_user()
    {
        using var client = _api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private static RegisterRequest NewRegistration(string email, string password = DefaultPassword) =>
        new(email, password, ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, PhoneNumber: null);

    private static async Task<string> RegisterAsync(HttpClient client, string? language = null)
    {
        var email = NewEmail();
        var request = NewRegistration(email) with { Language = language ?? SupportedLanguages.Thai };
        var response = await client.PostAsJsonAsync("/api/auth/register", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return email;
    }

    private static async Task LoginAsync(HttpClient client, string email, string password = DefaultPassword)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<CurrentUserResponse> GetCurrentUserAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CurrentUserResponse>())!;
    }

    private async Task<List<UserConsent>> ReadConsentsAsync(string email)
    {
        using var scope = _api.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var database = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
        var user = await users.FindByEmailAsync(email);
        return await database.UserConsents
            .Where(consent => consent.UserId == user!.Id)
            .ToListAsync();
    }

    private static (Guid UserId, string Token) ReadVerificationLink(string body)
    {
        var url = new Uri(body[body.IndexOf("http", StringComparison.Ordinal)..].Trim());
        var query = HttpUtility.ParseQueryString(url.Query);
        return (Guid.Parse(query["userId"]!), query["token"]!);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
