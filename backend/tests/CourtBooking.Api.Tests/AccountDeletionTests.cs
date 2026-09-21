using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>A person asking to be forgotten: PDPA, PRD 8, S-15.</summary>
public sealed class AccountDeletionTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task An_account_is_anonymised_and_cannot_sign_in_again()
    {
        var (client, email) = await scenario.SignedInClientWithEmailAsync();
        var me = await VenueScenario.ReadAsync<CurrentUserResponse>(await client.GetAsync("/api/auth/me"));

        var deleted = await DeleteAsync(client, VenueScenario.Password);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var row = await UserAsync(me.Id);
        Assert.NotNull(row.DeletedAt);
        Assert.Equal(AccountDeletion.Placeholder(me.Id), row.Email);
        Assert.Null(row.PasswordHash);
        Assert.Null(row.PhoneNumber);
        Assert.False(row.EmailConfirmed);

        // Signed out here, and the address no longer names anybody.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        var again = await api.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(email, VenueScenario.Password));
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    /// <summary>The address is free again: whoever owns it may start over (PDPA, PRD 8).</summary>
    [Fact]
    public async Task The_address_can_sign_up_again()
    {
        var (client, email) = await scenario.SignedInClientWithEmailAsync();
        await DeleteAsync(client, VenueScenario.Password);

        var registered = await api.CreateClient().PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(
                email, VenueScenario.Password, ApiFactory.PrivacyPolicyVersion, SupportedLanguages.Thai, null));

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
    }

    [Fact]
    public async Task It_asks_for_the_password_again()
    {
        var client = await scenario.SignedInClientAsync();

        var refused = await DeleteAsync(client, "NotThePassword1");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(AccountDeletionErrorCodes.WrongPassword, await refused.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    /// <summary>A venue cannot be left without its owner (PRD US-14).</summary>
    [Fact]
    public async Task A_venue_owner_cannot_leave_while_they_own_it()
    {
        var (owner, _, _) = await scenario.BookableVenueAsync();

        var refused = await DeleteAsync(owner, VenueScenario.Password);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(AccountDeletionErrorCodes.OwnsAVenue, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Not_with_hours_still_to_play()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        var refused = await DeleteAsync(booker, VenueScenario.Password);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(AccountDeletionErrorCodes.HasUpcomingBookings, await refused.ErrorCodeAsync());
    }

    /// <summary>Owed money back: once the address is gone nobody can send it (PRD 6.2).</summary>
    [Fact]
    public async Task Not_while_money_is_owed_back_to_them()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/reject",
            new RejectSlipRequest("ยอดไม่ตรง", PaymentReceived: true));

        var refused = await DeleteAsync(booker, VenueScenario.Password);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(AccountDeletionErrorCodes.HasMoneyPending, await refused.ErrorCodeAsync());

        // Paid back, they may go.
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds",
            new RecordRefundRequest(booking.TotalBaht, VenueScenario.Today, nameof(RefundMethod.Transfer), null));
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(booker, VenueScenario.Password)).StatusCode);
    }

    /// <summary>
    /// What they booked stays with the venue, as the venue's record, without their name on it:
    /// the day list shows nobody rather than a placeholder address (PRD 8).
    /// </summary>
    [Fact]
    public async Task Their_past_bookings_stay_but_name_nobody()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);
        await scenario.PlayOutAsync(booking.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(booker, VenueScenario.Password)).StatusCode);

        var stored = await scenario.StoredBookingAsync(booking.Id);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);

        var played = await ServiceDateAsync(booking.Id);
        var day = await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await owner.GetAsync($"/api/venues/{venue.Id}/bookings?date={played:yyyy-MM-dd}"));
        var row = Assert.Single(day, one => one.BookingId == booking.Id);
        Assert.Null(row.BookerEmail);
    }

    /// <summary>A member of staff leaves the venue's member list when they leave (PRD US-14).</summary>
    [Fact]
    public async Task A_member_of_staff_leaves_the_venue_too()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var staff = await scenario.StaffClientAsync(owner, venue.Id, nameof(Venues.VenuePermissions.VerifySlip));
        var before = await scenario.GetMembersAsync(owner, venue.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(staff, VenueScenario.Password)).StatusCode);

        var after = await scenario.GetMembersAsync(owner, venue.Id);
        Assert.Equal(before.Length - 1, after.Length);
    }

    // ---------------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, string password) =>
        client.PostAsJsonAsync("/api/auth/me/delete", new DeleteAccountRequest(password));

    private async Task<AppUser> UserAsync(Guid userId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.Users.AsNoTracking().SingleAsync(user => user.Id == userId);
    }

    private async Task<DateOnly> ServiceDateAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var starts = await database.BookingSlots
            .Where(slot => slot.BookingId == bookingId)
            .MinAsync(slot => slot.StartsAt);
        return Localization.PlatformRequirements.BangkokDateAndHour(starts).Date;
    }
}
