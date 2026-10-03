using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Data;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>Who could do what at a venue, and who changed it: PRD 8 (audit of permissions), US-14.</summary>
public sealed class MembershipAuditTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task A_seat_is_written_down_from_joining_through_every_change_to_leaving()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var (staff, staffEmail) = await scenario.SignedInClientWithEmailAsync();
        await scenario.InviteAndAcceptAsync(
            owner, staff, venue.Id, staffEmail, [nameof(VenuePermissions.VerifySlip)]);
        var ownerId = (await Me(owner)).Id;
        var staffId = (await Me(staff)).Id;

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.PutAsJsonAsync(
                $"/api/venues/{venue.Id}/members/{staffId}/permissions",
                new ChangePermissionsRequest(
                    [nameof(VenuePermissions.VerifySlip), nameof(VenuePermissions.ManageBookings)]))).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.DeleteAsync($"/api/venues/{venue.Id}/members/{staffId}")).StatusCode);

        var changes = await ChangesAsync(venue.Id);

        var joinedAsOwner = Assert.Single(changes, change => change.UserId == ownerId);
        Assert.Equal((MembershipChangeKind.Joined, VenueRole.Owner), (joinedAsOwner.Kind, joinedAsOwner.Role));

        var staffs = changes.Where(change => change.UserId == staffId).ToArray();
        Assert.Equal(
            [MembershipChangeKind.Joined, MembershipChangeKind.PermissionsChanged, MembershipChangeKind.Removed],
            staffs.Select(change => change.Kind));
        Assert.Equal(VenuePermissions.VerifySlip, staffs[0].PermissionsAfter);
        Assert.Equal(VenuePermissions.VerifySlip, staffs[1].PermissionsBefore);
        Assert.Equal(VenuePermissions.VerifySlip | VenuePermissions.ManageBookings, staffs[1].PermissionsAfter);
        Assert.Equal(ownerId, staffs[1].ChangedByUserId);
        Assert.Equal(ownerId, staffs[2].ChangedByUserId);
        Assert.Null(staffs[2].PermissionsAfter);
    }

    /// <summary>Saving the same permissions again is not a change, so nothing is written.</summary>
    [Fact]
    public async Task Saving_the_same_permissions_writes_nothing()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var staff = await scenario.StaffClientAsync(owner, venue.Id, nameof(VenuePermissions.VerifySlip));
        var staffId = (await Me(staff)).Id;
        var before = (await ChangesAsync(venue.Id)).Length;

        await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/members/{staffId}/permissions",
            new ChangePermissionsRequest([nameof(VenuePermissions.VerifySlip)]));

        Assert.Equal(before, (await ChangesAsync(venue.Id)).Length);
    }

    /// <summary>Leaving by being forgotten is written as the person leaving (PDPA, S-15).</summary>
    [Fact]
    public async Task A_member_who_deletes_their_account_is_written_down_as_leaving()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var staff = await scenario.StaffClientAsync(owner, venue.Id, nameof(VenuePermissions.VerifySlip));
        var staffId = (await Me(staff)).Id;

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await staff.PostAsJsonAsync(
                "/api/auth/me/delete", new DeleteAccountRequest(VenueScenario.Password))).StatusCode);

        var left = (await ChangesAsync(venue.Id)).Last(change => change.UserId == staffId);
        Assert.Equal(MembershipChangeKind.Left, left.Kind);
        Assert.Equal(staffId, left.ChangedByUserId);
        Assert.Equal(VenuePermissions.VerifySlip, left.PermissionsBefore);
    }

    private static async Task<CurrentUserResponse> Me(HttpClient client) =>
        await VenueScenario.ReadAsync<CurrentUserResponse>(await client.GetAsync("/api/auth/me"));

    private async Task<MembershipChange[]> ChangesAsync(Guid venueId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.MembershipChanges
            .AsNoTracking()
            .Where(change => change.VenueId == venueId)
            .OrderBy(change => change.ChangedAt)
            .ThenBy(change => change.Id)
            .ToArrayAsync();
    }
}
