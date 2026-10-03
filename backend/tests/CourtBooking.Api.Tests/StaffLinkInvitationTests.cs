using System.Net;
using System.Net.Http.Json;
using System.Web;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CourtBooking.Api.Tests;

/// <summary>
/// Inviting staff without an address (docs/plan/thai-fit.md T1): the owner gets a link to send over
/// LINE, the person signs up with a phone number through it, and signs in with that number.
/// </summary>
public sealed class StaffLinkInvitationTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    /// <summary>Sign-up as every deployment has it: closed.</summary>
    private WebApplicationFactory<Program> Closed() =>
        api.Api.WithWebHostBuilder(builder => builder.UseSetting("App:OpenSignUp", "false"));

    private static string NewPhone() => "08" + Random.Shared.NextInt64(10_000_000, 99_999_999);

    private async Task<(Guid VenueId, HttpClient Owner, VenueInvitationResponse Invitation, string Token)> LinkAsync()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        var invitation = await VenueScenario.ReadAsync<VenueInvitationResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/invitations", new InviteMemberRequest(null, null, "บอม", "081-234-5678")),
            HttpStatusCode.Created);
        var token = HttpUtility.ParseQueryString(new Uri(invitation.Link!).Query)["token"]!;
        return (venue.Id, owner, invitation, token);
    }

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient client, string phone, Guid? invitationId, string? token) =>
        client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(
                null,
                VenueScenario.Password,
                ApiFactory.PrivacyPolicyVersion,
                SupportedLanguages.Thai,
                phone,
                invitationId,
                token));

    [Fact]
    public async Task An_invitation_without_an_address_comes_back_as_a_link_once()
    {
        var (venueId, owner, invitation, _) = await LinkAsync();

        Assert.Null(invitation.Email);
        Assert.Equal("บอม", invitation.Name);
        Assert.Equal("0812345678", invitation.Phone);
        Assert.Contains($"/venue-invitation?invitationId={invitation.Id}&token=", invitation.Link);

        // The token is never stored, so the pending list cannot show the link again.
        var pending = await VenueScenario.ReadAsync<VenueInvitationResponse[]>(
            await owner.GetAsync($"/api/venues/{venueId}/invitations"));
        var listed = Assert.Single(pending);
        Assert.Equal("บอม", listed.Name);
        Assert.Null(listed.Link);
    }

    [Fact]
    public async Task An_invitation_needs_an_address_or_a_name()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);

        var nameless = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/invitations", new InviteMemberRequest(null, null));
        var badPhone = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/invitations", new InviteMemberRequest(null, null, "บอม", "12"));

        Assert.Equal(VenueErrorCodes.InvitationNeedsName, await nameless.ErrorCodeAsync());
        Assert.Equal(VenueErrorCodes.InvalidPhone, await badPhone.ErrorCodeAsync());
    }

    [Fact]
    public async Task The_link_opens_sign_up_with_a_phone_and_the_number_signs_in()
    {
        var (venueId, owner, invitation, token) = await LinkAsync();
        using var closed = Closed();
        var client = closed.CreateClient();
        var phone = NewPhone();

        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(client, phone, invitation.Id, token)).StatusCode);

        // Signed in by the number, typed the way people type it.
        var typed = $"{phone[..3]}-{phone[3..6]}-{phone[6..]}";
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(typed, VenueScenario.Password));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        var accepted = await client.PostAsJsonAsync(
            "/api/venues/invitations/accept", new AcceptInvitationRequest(invitation.Id, token));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var member = Assert.Single(
            await scenario.GetMembersAsync(owner, venueId), one => one.Role == nameof(VenueRole.Staff));
        Assert.Equal(string.Empty, member.Email);
        Assert.Equal("บอม", member.Name);
        Assert.Equal(phone, member.Phone);
    }

    [Fact]
    public async Task Without_the_link_a_phone_alone_opens_nothing()
    {
        var (_, _, invitation, _) = await LinkAsync();
        using var closed = Closed();

        var noLink = await RegisterAsync(closed.CreateClient(), NewPhone(), null, null);
        var wrongToken = await RegisterAsync(closed.CreateClient(), NewPhone(), invitation.Id, "not-the-token");

        Assert.Equal(AuthErrorCodes.InvalidEmail, await noLink.ErrorCodeAsync());
        Assert.Equal(AuthErrorCodes.InvalidEmail, await wrongToken.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_number_signs_up_once()
    {
        var (_, _, invitation, token) = await LinkAsync();
        var phone = NewPhone();

        Assert.Equal(
            HttpStatusCode.Created, (await RegisterAsync(api.CreateClient(), phone, invitation.Id, token)).StatusCode);
        var again = await RegisterAsync(api.CreateClient(), phone, invitation.Id, token);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(AuthErrorCodes.PhoneTaken, await again.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_link_with_no_address_is_one_seat_for_whoever_takes_it_first()
    {
        var (venueId, owner, invitation, token) = await LinkAsync();
        var (first, _) = await scenario.SignedInClientWithEmailAsync();
        var (second, _) = await scenario.SignedInClientWithEmailAsync();

        var taken = await first.PostAsJsonAsync(
            "/api/venues/invitations/accept", new AcceptInvitationRequest(invitation.Id, token));
        var late = await second.PostAsJsonAsync(
            "/api/venues/invitations/accept", new AcceptInvitationRequest(invitation.Id, token));

        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        Assert.Equal(VenueErrorCodes.InvitationInvalid, await late.ErrorCodeAsync());
        Assert.Equal(2, (await scenario.GetMembersAsync(owner, venueId)).Length);
    }

    [Fact]
    public async Task The_owner_takes_a_link_back_and_it_stops_working()
    {
        var (venueId, owner, invitation, token) = await LinkAsync();
        var (someone, _) = await scenario.SignedInClientWithEmailAsync();

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.DeleteAsync($"/api/venues/{venueId}/invitations/{invitation.Id}")).StatusCode);
        var refused = await someone.PostAsJsonAsync(
            "/api/venues/invitations/accept", new AcceptInvitationRequest(invitation.Id, token));

        Assert.Equal(VenueErrorCodes.InvitationInvalid, await refused.ErrorCodeAsync());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await owner.DeleteAsync($"/api/venues/{venueId}/invitations/{invitation.Id}")).StatusCode);
    }
}
