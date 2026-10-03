using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Data;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>
/// Sign-up closed, and the platform inviting owners (docs/plan/owner-complete.md 3a): with
/// App:OpenSignUp off, only an address somebody invited may become an account.
/// </summary>
public sealed class OwnerInvitationTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    /// <summary>The same app with sign-up as every deployment has it: closed.</summary>
    private WebApplicationFactory<Program> Closed() =>
        api.Api.WithWebHostBuilder(builder => builder.UseSetting("App:OpenSignUp", "false"));

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email) =>
        client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(
                email, VenueScenario.Password, ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, null));

    [Fact]
    public async Task With_sign_up_closed_a_stranger_cannot_make_an_account()
    {
        using var closed = Closed();

        var refused = await RegisterAsync(closed.CreateClient(), scenario.NewEmail());

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(AuthErrorCodes.InvitationRequired, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task The_platform_invites_an_owner_who_may_then_sign_up_once()
    {
        var admin = await scenario.PlatformAdminAsync();
        var email = scenario.NewEmail();

        var invited = await VenueScenario.ReadAsync<OwnerInvitationResponse>(
            await admin.PostAsJsonAsync(
                "/api/admin/owner-invitations", new InviteOwnerRequest(email, SupportedLanguages.Thai)),
            HttpStatusCode.Created);

        // They are told by email, with a link to sign up as that address.
        var letter = Assert.Single(api.Emails.To(email));
        Assert.Equal(AccountLetters.OwnerInvitationTemplate, letter.Template);
        Assert.Contains($"/register?email={Uri.EscapeDataString(email)}", letter.Body);

        using var closed = Closed();
        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(closed.CreateClient(), email)).StatusCode);

        // The invitation is spent: the list says so, and it opens no second account.
        var list = await VenueScenario.ReadAsync<OwnerInvitationResponse[]>(
            await admin.GetAsync("/api/admin/owner-invitations"));
        Assert.NotNull(Assert.Single(list, one => one.Id == invited.Id).AcceptedAt);
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await OwnerInvitations.InvitedAsync(
            database, email.ToUpperInvariant(), DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task Somebody_a_venue_invited_to_its_staff_may_sign_up_too()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var email = scenario.NewEmail();
        await scenario.InviteAsync(owner, venue.Id, email);

        using var closed = Closed();

        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(closed.CreateClient(), email)).StatusCode);
    }

    [Fact]
    public async Task An_invitation_that_ran_out_opens_nothing()
    {
        var admin = await scenario.PlatformAdminAsync();
        var email = scenario.NewEmail();
        await admin.PostAsJsonAsync("/api/admin/owner-invitations", new InviteOwnerRequest(email, null));
        using (var scope = api.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await database.OwnerInvitations
                .Where(one => one.Email == email)
                .ExecuteUpdateAsync(set => set.SetProperty(one => one.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        using var closed = Closed();
        var refused = await RegisterAsync(closed.CreateClient(), email);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Only_the_platform_invites_owners_and_not_an_address_that_has_an_account()
    {
        var (someone, email) = await scenario.SignedInClientWithEmailAsync();
        var refused = await someone.PostAsJsonAsync(
            "/api/admin/owner-invitations", new InviteOwnerRequest(scenario.NewEmail(), null));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var admin = await scenario.PlatformAdminAsync();
        var taken = await admin.PostAsJsonAsync("/api/admin/owner-invitations", new InviteOwnerRequest(email, null));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(AuthErrorCodes.AlreadyHasAccount, await taken.ErrorCodeAsync());

        var nonsense = await admin.PostAsJsonAsync("/api/admin/owner-invitations", new InviteOwnerRequest("not an email", null));
        Assert.Equal(HttpStatusCode.BadRequest, nonsense.StatusCode);
    }
}
