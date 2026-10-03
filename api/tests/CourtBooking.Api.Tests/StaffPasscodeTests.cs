using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>
/// Staff added by the owner with a six-digit passcode (thai-fit T1, replacing the LINE link): they
/// can work at once, accept the privacy policy themselves on their first sign-in, change the
/// passcode once in, and the owner can set a new one whenever it is lost.
/// </summary>
public sealed class StaffPasscodeTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    private static string NewPhone() => "08" + Random.Shared.NextInt64(10_000_000, 99_999_999);

    private async Task<(HttpClient Owner, Guid VenueId)> VenueAsync()
    {
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);
        return (owner, venue.Id);
    }

    private static async Task<StaffPasscodeResponse> AddAsync(HttpClient owner, Guid venueId, string phone) =>
        await VenueScenario.ReadAsync<StaffPasscodeResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/staff", new AddStaffRequest("ฝน", phone, ["ManageBookings"])),
            HttpStatusCode.Created);

    private Task<HttpResponseMessage> SignInAsync(HttpClient client, string phone, string passcode) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest(phone, passcode));

    [Fact]
    public async Task Added_staff_sign_in_at_once_and_accept_the_policy_themselves()
    {
        var (owner, venueId) = await VenueAsync();
        var phone = NewPhone();

        var added = await AddAsync(owner, venueId, phone);
        Assert.Matches("^[0-9]{6}$", added.Passcode);
        Assert.True(added.Member.UsesPasscode);
        Assert.True(added.Member.NeverSignedIn);
        Assert.Equal(["ManageBookings"], added.Member.Permissions);

        var staff = api.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await SignInAsync(staff, phone, added.Passcode!)).StatusCode);

        var me = await VenueScenario.ReadAsync<CurrentUserResponse>(await staff.GetAsync("/api/auth/me"));
        Assert.True(me.UsesPasscode);
        Assert.True(me.NeedsConsent);
        Assert.Equal("ฝน", me.DisplayName);
        // Working at once: the venue is theirs to read.
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync($"/api/venues/{venueId}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await staff.PostAsJsonAsync("/api/auth/me/consent", new ConsentRequest(ApiFactory.PrivacyPolicyVersion))).StatusCode);
        me = await VenueScenario.ReadAsync<CurrentUserResponse>(await staff.GetAsync("/api/auth/me"));
        Assert.False(me.NeedsConsent);
        var member = Assert.Single(await scenario.GetMembersAsync(owner, venueId), one => one.Phone == phone);
        Assert.False(member.NeverSignedIn);
    }

    [Fact]
    public async Task Staff_change_their_own_passcode_and_the_old_one_stops_working()
    {
        var (owner, venueId) = await VenueAsync();
        var phone = NewPhone();
        var added = await AddAsync(owner, venueId, phone);
        var staff = api.CreateClient();
        await SignInAsync(staff, phone, added.Passcode!);

        var wrong = await staff.PostAsJsonAsync("/api/auth/me/passcode", new ChangePasscodeRequest("000000", "123456"));
        Assert.Equal(AuthErrorCodes.WrongPasscode, await wrong.ErrorCodeAsync());
        var tooShort = await staff.PostAsJsonAsync("/api/auth/me/passcode", new ChangePasscodeRequest(added.Passcode, "12345"));
        Assert.Equal(AuthErrorCodes.InvalidPasscode, await tooShort.ErrorCodeAsync());

        var mine = added.Passcode == "246810" ? "135790" : "246810";
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await staff.PostAsJsonAsync("/api/auth/me/passcode", new ChangePasscodeRequest(added.Passcode, mine))).StatusCode);
        // The session that changed it goes on.
        Assert.Equal(HttpStatusCode.OK, (await staff.GetAsync("/api/auth/me")).StatusCode);

        Assert.NotEqual(HttpStatusCode.NoContent, (await SignInAsync(api.CreateClient(), phone, added.Passcode!)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SignInAsync(api.CreateClient(), phone, mine)).StatusCode);
    }

    [Fact]
    public async Task The_owner_sets_a_new_passcode_only_for_staff_who_sign_in_with_one()
    {
        var (owner, venueId) = await VenueAsync();
        var phone = NewPhone();
        var added = await AddAsync(owner, venueId, phone);

        var reset = await VenueScenario.ReadAsync<StaffPasscodeResponse>(
            await owner.PostAsync($"/api/venues/{venueId}/members/{added.Member.UserId}/passcode", null));
        Assert.Matches("^[0-9]{6}$", reset.Passcode);
        if (reset.Passcode != added.Passcode)
        {
            Assert.NotEqual(HttpStatusCode.NoContent, (await SignInAsync(api.CreateClient(), phone, added.Passcode!)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await SignInAsync(api.CreateClient(), phone, reset.Passcode!)).StatusCode);

        // A member with an address has a password of their own, which no owner chooses.
        await scenario.StaffClientAsync(owner, venueId, "ManageBookings");
        var withAddress = (await scenario.GetMembersAsync(owner, venueId)).First(one => one.Email != string.Empty && one.Role == "Staff");
        var refused = await owner.PostAsync($"/api/venues/{venueId}/members/{withAddress.UserId}/passcode", null);
        Assert.Equal(VenueErrorCodes.NotAPasscodeAccount, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task One_phone_is_one_account_and_another_venue_does_not_reset_it()
    {
        var (owner, venueId) = await VenueAsync();
        var phone = NewPhone();
        var added = await AddAsync(owner, venueId, phone);

        var twice = await owner.PostAsJsonAsync(
            $"/api/venues/{venueId}/staff", new AddStaffRequest("ฝน", phone, null));
        Assert.Equal(VenueErrorCodes.AlreadyMember, await twice.ErrorCodeAsync());

        // Another owner's venue gets the same person, with the passcode they already have.
        var (otherOwner, otherVenue) = await VenueAsync();
        var joined = await AddAsync(otherOwner, otherVenue, phone);
        Assert.Null(joined.Passcode);
        Assert.Equal(HttpStatusCode.NoContent, (await SignInAsync(api.CreateClient(), phone, added.Passcode!)).StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_adds_staff()
    {
        var (owner, venueId) = await VenueAsync();
        var staff = await scenario.StaffClientAsync(owner, venueId, "ManageBookings");

        var refused = await staff.PostAsJsonAsync(
            $"/api/venues/{venueId}/staff", new AddStaffRequest("ฝน", NewPhone(), null));
        var nameless = await owner.PostAsJsonAsync(
            $"/api/venues/{venueId}/staff", new AddStaffRequest(" ", NewPhone(), null));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(VenueErrorCodes.StaffNeedsName, await nameless.ErrorCodeAsync());
    }
}
