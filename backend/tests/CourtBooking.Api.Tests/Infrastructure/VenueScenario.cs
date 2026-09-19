using System.Net;
using System.Net.Http.Json;
using System.Web;
using CourtBooking.Api.Data;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
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

    /// <summary>
    /// The day the platform is on, which is the day the server will compare a date against. Tests
    /// that publish settings have to agree with it or the settings are dated in the past.
    /// </summary>
    public static DateOnly Today => PlatformRequirements.BangkokToday(TimeProvider.System);

    /// <summary>New details for a venue whose address the test does not care about.</summary>
    public static UpdateVenueRequest Details(string name) =>
        new(name, "2 ถนนใหม่", "ปทุมวัน", "กรุงเทพมหานคร");

    /// <summary>The same hours seven days a week, with one day optionally closed.</summary>
    public static OpeningHoursDayRequest[] Week(int opens, int closes, DayOfWeek? closedOn = null) =>
        Enum.GetValues<DayOfWeek>()
            .Select(day => day == closedOn
                ? new OpeningHoursDayRequest(day.ToString(), null, null)
                : new OpeningHoursDayRequest(day.ToString(), opens, closes))
            .ToArray();

    public static async Task SetHoursAsync(
        HttpClient client,
        Guid venueId,
        DateOnly from,
        int opens = 6,
        int closes = 22,
        DayOfWeek? closedOn = null)
    {
        var response = await client.PutAsJsonAsync(
            $"/api/venues/{venueId}/opening-hours",
            new SetOpeningHoursRequest(from, Week(opens, closes, closedOn)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>One price for every hour of every day, which is what most tests want.</summary>
    public static PriceBandRequest[] AllWeek(int from, int to, decimal baht) =>
        Enum.GetValues<DayOfWeek>()
            .Select(day => new PriceBandRequest(day.ToString(), from, to, baht))
            .ToArray();

    public static async Task<PriceListResponse> SetPricesAsync(
        HttpClient client,
        Guid venueId,
        IEnumerable<PriceBandRequest> bands) =>
        await ReadAsync<PriceListResponse>(
            await client.PutAsJsonAsync($"/api/venues/{venueId}/prices", new SetPricesRequest(bands.ToArray())));
}
