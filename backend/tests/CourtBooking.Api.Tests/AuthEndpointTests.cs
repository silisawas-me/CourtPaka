using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CourtBooking.Api.Tests;

public sealed class AuthEndpointTests : IClassFixture<PostgresFixture>, IAsyncLifetime, IDisposable
{
    private const string PolicyVersion = "2026-09-01";

    private readonly FakeEmailSender _emails = new();
    private readonly ApiFactory _factory;

    public AuthEndpointTests(PostgresFixture postgres)
    {
        _factory = new ApiFactory(
            postgres.ConnectionString,
            services => services.Replace(ServiceDescriptor.Singleton<IEmailSender>(_emails)));
    }

    public Task InitializeAsync() => _factory.MigrateAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Registration_creates_an_unverified_account_and_emails_a_verification_link()
    {
        using var client = _factory.CreateClient();
        var email = NewEmail();

        var response = await client.PostAsJsonAsync("/api/auth/register", NewRegistration(email));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("verify", _emails.LastTo(email).Subject, StringComparison.OrdinalIgnoreCase);

        await LoginAsync(client, email);
        var me = await GetCurrentUserAsync(client);
        Assert.False(me.EmailConfirmed);
        Assert.Equal(SupportedLanguages.Thai, me.Language);
    }

    [Fact]
    public async Task Verifying_the_emailed_token_confirms_the_account()
    {
        using var client = _factory.CreateClient();
        var email = NewEmail();
        await client.PostAsJsonAsync("/api/auth/register", NewRegistration(email));
        var (userId, token) = ReadVerificationLink(_emails.LastTo(email).Body);

        var response = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest(userId, token));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await LoginAsync(client, email);
        Assert.True((await GetCurrentUserAsync(client)).EmailConfirmed);
    }

    [Fact]
    public async Task A_tampered_verification_token_is_rejected()
    {
        using var client = _factory.CreateClient();
        var email = NewEmail();
        await client.PostAsJsonAsync("/api/auth/register", NewRegistration(email));
        var (userId, token) = ReadVerificationLink(_emails.LastTo(email).Body);

        var response = await client.PostAsJsonAsync(
            "/api/auth/verify-email", new VerifyEmailRequest(userId, token[..^4] + "AAAA"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AuthErrorCodes.InvalidVerificationToken, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Registering_an_address_that_already_exists_does_not_reveal_it()
    {
        using var client = _factory.CreateClient();
        var email = NewEmail();
        await client.PostAsJsonAsync("/api/auth/register", NewRegistration(email));

        var response = await client.PostAsJsonAsync("/api/auth/register", NewRegistration(email, "An0therPass!"));

        // Same status as a fresh registration; the address owner is told by email instead.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("already exists", _emails.LastTo(email).Subject, StringComparison.OrdinalIgnoreCase);

        // The original password still works, so the second attempt did not touch the account.
        await LoginAsync(client, email);
    }

    [Theory]
    [InlineData("short1", AuthErrorCodes.WeakPassword)]
    [InlineData("12345678", AuthErrorCodes.WeakPassword)]
    public async Task Weak_passwords_are_rejected(string password, string expectedCode)
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", NewRegistration(NewEmail(), password));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_unsupported_language_is_rejected()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register", NewRegistration(NewEmail()) with { Language = "fr" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AuthErrorCodes.UnsupportedLanguage, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Registration_requires_accepting_the_privacy_policy()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register", NewRegistration(NewEmail()) with { PrivacyPolicyVersion = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AuthErrorCodes.PrivacyPolicyRequired, await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task Five_failed_sign_ins_lock_the_account_for_the_correct_password_too()
    {
        using var client = _factory.CreateClient();
        var email = NewEmail();
        await client.PostAsJsonAsync("/api/auth/register", NewRegistration(email));

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var failure = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "WrongPassword1"));
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        // The fifth failure is the one that locks the account.
        var fifth = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "WrongPassword1"));
        Assert.Equal(HttpStatusCode.Locked, fifth.StatusCode);

        var locked = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, DefaultPassword));

        Assert.Equal(HttpStatusCode.Locked, locked.StatusCode);
        Assert.Equal(AuthErrorCodes.AccountLocked, await ReadErrorCodeAsync(locked));
    }

    [Fact]
    public async Task The_chosen_language_is_stored_and_can_be_changed()
    {
        using var client = _factory.CreateClient();
        var email = NewEmail();
        await client.PostAsJsonAsync(
            "/api/auth/register", NewRegistration(email) with { Language = SupportedLanguages.English });
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
    public async Task Signing_out_ends_the_session()
    {
        using var client = _factory.CreateClient();
        var email = NewEmail();
        await client.PostAsJsonAsync("/api/auth/register", NewRegistration(email));
        await LoginAsync(client, email);

        var loggedOut = await client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_callers_cannot_read_the_current_user()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    private const string DefaultPassword = "CorrectHorse1";

    private static string NewEmail() => $"user-{Guid.NewGuid():N}@example.com";

    private static RegisterRequest NewRegistration(string email, string password = DefaultPassword) =>
        new(email, password, PolicyVersion, SupportedLanguages.Thai, PhoneNumber: null);

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
