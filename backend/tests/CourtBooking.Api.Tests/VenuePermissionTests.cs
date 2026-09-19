using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

[Collection(DatabaseCollection.Name)]
public sealed class VenuePermissionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "CorrectHorse1";

    private readonly AuthApiFixture _api = new(postgres);

    public Task InitializeAsync() => _api.InitializeAsync();

    public Task DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Creating_a_venue_makes_the_applicant_its_owner_with_every_permission()
    {
        var owner = await SignedInClientAsync();

        var venue = await CreateVenueAsync(owner);

        var members = await GetMembersAsync(owner, venue.Id);
        var member = Assert.Single(members);
        Assert.Equal(nameof(VenueRole.Owner), member.Role);
        Assert.Equal(
            new[] { "VerifySlip", "ManageBookings", "CloseCourt", "ViewReports", "ManageSettings" }.Order(),
            member.Permissions.Order());
    }

    [Fact]
    public async Task Two_venues_cannot_share_a_code()
    {
        var first = await SignedInClientAsync();
        var second = await SignedInClientAsync();
        var venue = await CreateVenueAsync(first);

        var response = await second.PostAsJsonAsync(
            "/api/venues", new CreateVenueRequest(venue.Code, "Another venue"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_member_of_one_venue_cannot_read_another_venue()
    {
        var owner = await SignedInClientAsync();
        var outsider = await SignedInClientAsync();
        var venue = await CreateVenueAsync(owner);
        await CreateVenueAsync(outsider);

        var read = await outsider.GetAsync($"/api/venues/{venue.Id}");
        var members = await outsider.GetAsync($"/api/venues/{venue.Id}/members");
        var rename = await outsider.PutAsJsonAsync($"/api/venues/{venue.Id}", new RenameVenueRequest("Taken over"));

        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, members.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
    }

    [Fact]
    public async Task Staff_get_the_default_permissions_and_are_refused_the_rest()
    {
        var owner = await SignedInClientAsync();
        var (staff, staffEmail) = await SignedInClientWithEmailAsync();
        var venue = await CreateVenueAsync(owner);

        var invite = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/members", new InviteMemberRequest(staffEmail, Permissions: null));
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);

        // Default staff may work the desk...
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/venues/{venue.Id}")).StatusCode);
        // ...but settings need a permission they were not given.
        var rename = await staff.PutAsJsonAsync($"/api/venues/{venue.Id}", new RenameVenueRequest("New name"));
        Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
    }

    [Fact]
    public async Task The_owner_can_grant_a_permission_and_take_it_away_again()
    {
        var owner = await SignedInClientAsync();
        var (staff, staffEmail) = await SignedInClientWithEmailAsync();
        var venue = await CreateVenueAsync(owner);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/members", new InviteMemberRequest(staffEmail, Permissions: null));
        var staffId = (await GetMembersAsync(owner, venue.Id)).Single(m => m.Email == staffEmail).UserId;

        var granted = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/members/{staffId}/permissions",
            new ChangePermissionsRequest(VenuePermissions.StaffDefault | VenuePermissions.ManageSettings));
        Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);

        var allowed = await staff.PutAsJsonAsync($"/api/venues/{venue.Id}", new RenameVenueRequest("Renamed"));
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);

        await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/members/{staffId}/permissions",
            new ChangePermissionsRequest(VenuePermissions.StaffDefault));

        var refused = await staff.PutAsJsonAsync($"/api/venues/{venue.Id}", new RenameVenueRequest("Renamed again"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Staff_cannot_manage_the_member_list()
    {
        var owner = await SignedInClientAsync();
        var (staff, staffEmail) = await SignedInClientWithEmailAsync();
        var (_, outsiderEmail) = await SignedInClientWithEmailAsync();
        var venue = await CreateVenueAsync(owner);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/members", new InviteMemberRequest(staffEmail, Permissions: null));
        var staffId = (await GetMembersAsync(owner, venue.Id)).Single(m => m.Email == staffEmail).UserId;

        var invite = await staff.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/members", new InviteMemberRequest(outsiderEmail, VenuePermissions.All));
        var escalate = await staff.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/members/{staffId}/permissions",
            new ChangePermissionsRequest(VenuePermissions.All));
        var removeOwner = await staff.DeleteAsync($"/api/venues/{venue.Id}/members/{staffId}");

        Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, escalate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, removeOwner.StatusCode);
    }

    [Fact]
    public async Task The_owner_cannot_be_stripped_of_permissions_or_removed()
    {
        var owner = await SignedInClientAsync();
        var venue = await CreateVenueAsync(owner);
        var ownerId = (await GetMembersAsync(owner, venue.Id)).Single().UserId;

        var change = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/members/{ownerId}/permissions",
            new ChangePermissionsRequest(VenuePermissions.None));
        var remove = await owner.DeleteAsync($"/api/venues/{venue.Id}/members/{ownerId}");

        Assert.Equal(HttpStatusCode.Conflict, change.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
    }

    [Fact]
    public async Task Removing_a_member_ends_their_access_at_once()
    {
        var owner = await SignedInClientAsync();
        var (staff, staffEmail) = await SignedInClientWithEmailAsync();
        var venue = await CreateVenueAsync(owner);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/members", new InviteMemberRequest(staffEmail, Permissions: null));
        var staffId = (await GetMembersAsync(owner, venue.Id)).Single(m => m.Email == staffEmail).UserId;
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/venues/{venue.Id}")).StatusCode);

        var removed = await owner.DeleteAsync($"/api/venues/{venue.Id}/members/{staffId}");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync($"/api/venues/{venue.Id}")).StatusCode);
    }

    [Fact]
    public async Task Only_venues_the_caller_belongs_to_are_listed()
    {
        var owner = await SignedInClientAsync();
        var outsider = await SignedInClientAsync();
        var mine = await CreateVenueAsync(owner);
        await CreateVenueAsync(outsider);

        var listed = await owner.GetFromJsonAsync<VenueResponse[]>("/api/venues/mine");

        Assert.Equal([mine.Id], listed!.Select(venue => venue.Id));
    }

    [Fact]
    public async Task Anonymous_callers_are_refused()
    {
        using var anonymous = _api.CreateClient();
        var owner = await SignedInClientAsync();
        var venue = await CreateVenueAsync(owner);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/venues/{venue.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/venues/mine")).StatusCode);
    }

    [Fact]
    public async Task Inviting_an_address_with_no_account_says_so()
    {
        var owner = await SignedInClientAsync();
        var venue = await CreateVenueAsync(owner);

        var response = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/members", new InviteMemberRequest("nobody@example.com", Permissions: null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string NewEmail() => $"venue-{Guid.NewGuid():N}@example.com";

    private static string NewCode() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<HttpClient> SignedInClientAsync() => (await SignedInClientWithEmailAsync()).Client;

    private async Task<(HttpClient Client, string Email)> SignedInClientWithEmailAsync()
    {
        var client = _api.CreateClient();
        var email = NewEmail();
        var registration = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(email, Password, ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, null));
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        return (client, email);
    }

    private static async Task<VenueResponse> CreateVenueAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/venues", new CreateVenueRequest(NewCode(), "Smash Court"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VenueResponse>())!;
    }

    private static async Task<VenueMemberResponse[]> GetMembersAsync(HttpClient client, Guid venueId)
    {
        var response = await client.GetAsync($"/api/venues/{venueId}/members");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VenueMemberResponse[]>())!;
    }
}
