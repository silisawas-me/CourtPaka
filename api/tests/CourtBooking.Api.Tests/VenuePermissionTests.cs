using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class VenuePermissionTests(ApiTestFixture api)
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task Creating_a_venue_makes_the_applicant_its_owner_with_every_permission()
    {
        var owner = await scenario.SignedInClientAsync();

        var venue = await scenario.CreateVenueAsync(owner);

        var member = Assert.Single(await scenario.GetMembersAsync(owner, venue.Id));
        Assert.Equal(nameof(VenueRole.Owner), member.Role);
        Assert.Equal(
            VenuePermissionSet.Grantable.Select(permission => permission.ToString()).Order(),
            member.Permissions.Order());
    }

    [Fact]
    public async Task Two_venues_cannot_share_a_code()
    {
        var first = await scenario.SignedInClientAsync();
        var second = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(first);

        var response = await second.PostAsJsonAsync(
            "/api/venues", VenueScenario.Application(venue.Code));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_member_of_one_venue_cannot_reach_another_venue()
    {
        var owner = await scenario.SignedInClientAsync();
        var outsider = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        await scenario.CreateVenueAsync(outsider);

        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/venues/{venue.Id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/venues/{venue.Id}/members")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await outsider.PutAsJsonAsync($"/api/venues/{venue.Id}", VenueScenario.Details("Taken over"))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await outsider.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/invitations",
                new InviteMemberRequest("someone@example.com", null))).StatusCode);
    }

    [Fact]
    public async Task An_invited_person_joins_as_staff_with_the_default_permissions()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        var (staff, staffEmail) = await scenario.SignedInClientWithEmailAsync();

        await scenario.InviteAndAcceptAsync(owner, staff, venue.Id, staffEmail);

        var member = (await scenario.GetMembersAsync(owner, venue.Id)).Single(m => m.Email == staffEmail);
        Assert.Equal(nameof(VenueRole.Staff), member.Role);
        Assert.Equal(
            new[] { "VerifySlip", "ManageBookings", "CloseCourt" }.Order(), member.Permissions.Order());

        // Default staff may work the desk...
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/venues/{venue.Id}")).StatusCode);
        // ...but settings need a permission they were not given.
        var rename = await staff.PutAsJsonAsync($"/api/venues/{venue.Id}", VenueScenario.Details("New name"));
        Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
    }

    [Fact]
    public async Task An_invitation_can_only_be_accepted_by_the_address_it_names()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        var (_, invitedEmail) = await scenario.SignedInClientWithEmailAsync();
        var (otherPerson, _) = await scenario.SignedInClientWithEmailAsync();

        var invitation = await scenario.InviteAsync(owner, venue.Id, invitedEmail);
        var token = scenario.ReadInvitationToken(invitedEmail);

        var response = await otherPerson.PostAsJsonAsync(
            "/api/venues/invitations/accept", new AcceptInvitationRequest(invitation.Id, token));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherPerson.GetAsync($"/api/venues/{venue.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_tampered_invitation_token_is_refused()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        var (staff, staffEmail) = await scenario.SignedInClientWithEmailAsync();
        var invitation = await scenario.InviteAsync(owner, venue.Id, staffEmail);

        var response = await staff.PostAsJsonAsync(
            "/api/venues/invitations/accept", new AcceptInvitationRequest(invitation.Id, "not-the-token"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync($"/api/venues/{venue.Id}")).StatusCode);
    }

    [Fact]
    public async Task An_invitation_cannot_be_used_twice()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        var (staff, staffEmail) = await scenario.SignedInClientWithEmailAsync();
        var invitation = await scenario.InviteAndAcceptAsync(owner, staff, venue.Id, staffEmail);

        var again = await staff.PostAsJsonAsync(
            "/api/venues/invitations/accept",
            new AcceptInvitationRequest(invitation.Id, scenario.ReadInvitationToken(staffEmail)));

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task Inviting_someone_who_is_already_a_member_is_refused()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        var (staff, staffEmail) = await scenario.SignedInClientWithEmailAsync();
        await scenario.InviteAndAcceptAsync(owner, staff, venue.Id, staffEmail);

        var response = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/invitations", new InviteMemberRequest(staffEmail, null));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task The_owner_can_grant_a_permission_and_take_it_away_again()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        var (staff, staffEmail) = await scenario.SignedInClientWithEmailAsync();
        await scenario.InviteAndAcceptAsync(owner, staff, venue.Id, staffEmail);
        var staffId = (await scenario.GetMembersAsync(owner, venue.Id)).Single(m => m.Email == staffEmail).UserId;

        var granted = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/members/{staffId}/permissions",
            new ChangePermissionsRequest(["VerifySlip", "ManageBookings", "CloseCourt", "ManageSettings"]));
        Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);

        var allowed = await staff.PutAsJsonAsync($"/api/venues/{venue.Id}", VenueScenario.Details("Renamed"));
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);

        await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/members/{staffId}/permissions",
            new ChangePermissionsRequest(["VerifySlip", "ManageBookings", "CloseCourt"]));

        var refused = await staff.PutAsJsonAsync($"/api/venues/{venue.Id}", VenueScenario.Details("Renamed again"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Staff_cannot_manage_the_member_list()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        var (staff, staffEmail) = await scenario.SignedInClientWithEmailAsync();
        var (_, outsiderEmail) = await scenario.SignedInClientWithEmailAsync();
        await scenario.InviteAndAcceptAsync(owner, staff, venue.Id, staffEmail);
        var staffId = (await scenario.GetMembersAsync(owner, venue.Id)).Single(m => m.Email == staffEmail).UserId;

        var invite = await staff.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/invitations", new InviteMemberRequest(outsiderEmail, VenuePermissionSet.Describe(VenuePermissions.All)));
        var escalate = await staff.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/members/{staffId}/permissions",
            new ChangePermissionsRequest(VenuePermissionSet.Describe(VenuePermissions.All)));
        var remove = await staff.DeleteAsync($"/api/venues/{venue.Id}/members/{staffId}");
        var invitations = await staff.GetAsync($"/api/venues/{venue.Id}/invitations");

        Assert.Equal(HttpStatusCode.Forbidden, invite.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, escalate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, invitations.StatusCode);
    }

    [Fact]
    public async Task The_owner_cannot_be_stripped_of_permissions_or_removed()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        var ownerId = (await scenario.GetMembersAsync(owner, venue.Id)).Single().UserId;

        var change = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/members/{ownerId}/permissions",
            new ChangePermissionsRequest([]));
        var remove = await owner.DeleteAsync($"/api/venues/{venue.Id}/members/{ownerId}");

        Assert.Equal(HttpStatusCode.Conflict, change.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
    }

    [Fact]
    public async Task Removing_a_member_ends_their_access_at_once()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        var (staff, staffEmail) = await scenario.SignedInClientWithEmailAsync();
        await scenario.InviteAndAcceptAsync(owner, staff, venue.Id, staffEmail);
        var staffId = (await scenario.GetMembersAsync(owner, venue.Id)).Single(m => m.Email == staffEmail).UserId;
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/venues/{venue.Id}")).StatusCode);

        var removed = await owner.DeleteAsync($"/api/venues/{venue.Id}/members/{staffId}");

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync($"/api/venues/{venue.Id}")).StatusCode);
    }

    [Fact]
    public async Task Only_venues_the_caller_belongs_to_are_listed()
    {
        var owner = await scenario.SignedInClientAsync();
        var outsider = await scenario.SignedInClientAsync();
        var mine = await scenario.CreateVenueAsync(owner);
        await scenario.CreateVenueAsync(outsider);

        var listed = await owner.GetFromJsonAsync<VenueResponse[]>("/api/venues/mine");

        Assert.Equal([mine.Id], listed!.Select(venue => venue.Id));
    }

    [Fact]
    public async Task Permissions_outside_the_known_set_are_refused()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);

        var response = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/invitations",
            new InviteMemberRequest("someone@example.com", ["NotAPermission"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null, "Smash Court", VenueErrorCodes.InvalidCode)]
    [InlineData("", "Smash Court", VenueErrorCodes.InvalidCode)]
    [InlineData("AB", "Smash Court", VenueErrorCodes.InvalidCode)]
    [InlineData("TOOLONGCODE", "Smash Court", VenueErrorCodes.InvalidCode)]
    [InlineData("AB CD", "Smash Court", VenueErrorCodes.InvalidCode)]
    [InlineData("ABC123", null, VenueErrorCodes.InvalidName)]
    [InlineData("ABC124", "   ", VenueErrorCodes.InvalidName)]
    public async Task A_malformed_venue_is_refused_with_a_code(string? code, string? name, string expected)
    {
        var owner = await scenario.SignedInClientAsync();

        var response = await owner.PostAsJsonAsync("/api/venues", new { code, name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expected, await response.ErrorCodeAsync());
    }

    // No address at all is an invitation by link (thai-fit T1) — StaffLinkInvitationTests.
    [Theory]
    [InlineData("not an address")]
    [InlineData("bom@")]
    public async Task A_malformed_invitation_address_is_refused_with_a_code(string? email)
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);

        var response = await owner.PostAsJsonAsync($"/api/venues/{venue.Id}/invitations", new { email });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(VenueErrorCodes.InvalidEmail, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Renaming_with_an_empty_name_is_refused_with_a_code()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);

        var response = await owner.PutAsJsonAsync($"/api/venues/{venue.Id}", new { name = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(VenueErrorCodes.InvalidName, await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Re_inviting_the_same_address_replaces_the_pending_invitation()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        var (staff, staffEmail) = await scenario.SignedInClientWithEmailAsync();
        var first = await scenario.InviteAsync(owner, venue.Id, staffEmail);
        var firstToken = scenario.ReadInvitationToken(staffEmail);

        var second = await scenario.InviteAsync(owner, venue.Id, staffEmail, VenuePermissionSet.Describe(VenuePermissions.All));

        var pending = await owner.GetFromJsonAsync<VenueInvitationResponse[]>(
            $"/api/venues/{venue.Id}/invitations");
        Assert.Equal([second.Id], pending!.Select(invitation => invitation.Id));

        // The replaced link no longer works.
        var replaced = await staff.PostAsJsonAsync(
            "/api/venues/invitations/accept", new AcceptInvitationRequest(first.Id, firstToken));
        Assert.Equal(HttpStatusCode.BadRequest, replaced.StatusCode);
    }

    [Fact]
    public async Task Re_inviting_the_same_address_in_different_case_still_replaces_the_invitation()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        var (staff, staffEmail) = await scenario.SignedInClientWithEmailAsync();
        var permissive = await scenario.InviteAsync(owner, venue.Id, staffEmail.ToUpperInvariant(), VenuePermissionSet.Describe(VenuePermissions.All));
        var permissiveToken = scenario.ReadInvitationToken(staffEmail.ToUpperInvariant());

        // The owner changes their mind and re-invites the same mailbox, typed differently.
        await scenario.InviteAsync(owner, venue.Id, staffEmail.ToLowerInvariant(), ["VerifySlip"]);

        var replaced = await staff.PostAsJsonAsync(
            "/api/venues/invitations/accept", new AcceptInvitationRequest(permissive.Id, permissiveToken));

        Assert.Equal(HttpStatusCode.BadRequest, replaced.StatusCode);
    }

    [Fact]
    public async Task An_invitation_cannot_be_accepted_into_a_suspended_venue()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        var (staff, staffEmail) = await scenario.SignedInClientWithEmailAsync();
        var invitation = await scenario.InviteAsync(owner, venue.Id, staffEmail);
        var token = scenario.ReadInvitationToken(staffEmail);
        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);

        var response = await staff.PostAsJsonAsync(
            "/api/venues/invitations/accept", new AcceptInvitationRequest(invitation.Id, token));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(VenueErrorCodes.NotApproved, await response.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync($"/api/venues/{venue.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_suspended_venue_can_be_read_but_not_changed()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/venues/{venue.Id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await owner.PutAsJsonAsync($"/api/venues/{venue.Id}", VenueScenario.Details("New name"))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/invitations",
                new InviteMemberRequest("someone@example.com", null))).StatusCode);
    }

    [Fact]
    public async Task Anonymous_callers_are_refused()
    {
        using var anonymous = api.CreateClient();
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/venues/{venue.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/venues/mine")).StatusCode);
    }

}
