using System.Net;
using System.Net.Http.Json;
using System.Web;
using CourtBooking.Api.Data;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests.Infrastructure;

/// <summary>
/// Getting to the point a venue test starts from: a signed-in person, a venue they own, and staff
/// who joined it. Every test in the run shares one API, so each scenario makes its own addresses.
/// </summary>
public sealed class VenueScenario(ApiTestFixture api)
{
    public const string Password = "CorrectHorse1";

    public string NewEmail() => $"venue-{Guid.NewGuid():N}@example.com";

    public string NewCode() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    public async Task<HttpClient> SignedInClientAsync() => (await SignedInClientWithEmailAsync()).Client;

    public async Task<(HttpClient Client, string Email)> SignedInClientWithEmailAsync()
    {
        var client = api.CreateClient();
        var email = NewEmail();
        var registration = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, Password, ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, null));
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        return (client, email);
    }

    public async Task<VenueResponse> CreateVenueAsync(HttpClient client) =>
        await ReadAsync<VenueResponse>(
            await client.PostAsJsonAsync("/api/venues", new CreateVenueRequest(NewCode(), "Smash Court", "1 ถนนทดสอบ", "บางรัก", "กรุงเทพมหานคร")),
            HttpStatusCode.Created);

    /// <summary>
    /// Checks the status the endpoint promised and hands back the body, so a test that is about
    /// something else does not spell out both every time.
    /// </summary>
    public static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public async Task<VenueInvitationResponse> InviteAsync(
        HttpClient owner,
        Guid venueId,
        string email,
        string[]? permissions = null) =>
        await ReadAsync<VenueInvitationResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/invitations", new InviteMemberRequest(email, permissions)),
            HttpStatusCode.Created);

    public async Task<VenueInvitationResponse> InviteAndAcceptAsync(
        HttpClient owner,
        HttpClient invited,
        Guid venueId,
        string email,
        string[]? permissions = null)
    {
        var invitation = await InviteAsync(owner, venueId, email, permissions);
        var accepted = await invited.PostAsJsonAsync(
            "/api/venues/invitations/accept",
            new AcceptInvitationRequest(invitation.Id, ReadInvitationToken(email)));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        return invitation;
    }

    /// <summary>A signed-in staff member of the venue, holding exactly the permissions named.</summary>
    public async Task<HttpClient> StaffClientAsync(HttpClient owner, Guid venueId, params string[] permissions)
    {
        var (client, email) = await SignedInClientWithEmailAsync();
        await InviteAndAcceptAsync(owner, client, venueId, email, permissions);
        return client;
    }

    public string ReadInvitationToken(string email)
    {
        var body = api.Emails.LastTo(email).Body;
        var url = new Uri(body[body.IndexOf("http", StringComparison.Ordinal)..].Trim());
        return HttpUtility.ParseQueryString(url.Query)["token"]!;
    }

    public async Task SetStatusAsync(Guid venueId, VenueStatus status)
    {
        // Platform Admin approval arrives with US-20; until then the test sets the status directly.
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Venues
            .Where(venue => venue.Id == venueId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(venue => venue.Status, status));
    }

    public async Task<VenueMemberResponse[]> GetMembersAsync(HttpClient client, Guid venueId) =>
        await ReadAsync<VenueMemberResponse[]>(await client.GetAsync($"/api/venues/{venueId}/members"));
}
