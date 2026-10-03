using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
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

        // The name they gave the app is what the counter calls them — until they are forgotten.
        var bookerId = (await scenario.StoredBookingAsync(booking.Id)).BookerUserId!.Value;
        using (var scope = api.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await database.Users
                .Where(user => user.Id == bookerId)
                .ExecuteUpdateAsync(set => set.SetProperty(user => user.DisplayName, "คุณแพร"));
        }

        var played = await ServiceDateAsync(booking.Id);
        async Task<VenueBookingResponse> RowAsync() => Assert.Single(
            await VenueScenario.ReadAsync<VenueBookingResponse[]>(
                await owner.GetAsync($"/api/venues/{venue.Id}/bookings?date={played:yyyy-MM-dd}")),
            one => one.BookingId == booking.Id);
        Assert.Equal("คุณแพร", (await RowAsync()).BookerName);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(booker, VenueScenario.Password)).StatusCode);

        var stored = await scenario.StoredBookingAsync(booking.Id);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);

        var row = await RowAsync();
        Assert.Null(row.BookerEmail);
        Assert.Null(row.BookerName);
        using (var scope = api.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Null(await database.Users
                .Where(user => user.Id == bookerId)
                .Select(user => user.DisplayName)
                .SingleAsync());
        }
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

    [Fact]
    public async Task A_platform_admin_cannot_delete_their_account_here()
    {
        var admin = await scenario.PlatformAdminAsync();

        var refused = await DeleteAsync(admin, VenueScenario.Password);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(AccountDeletionErrorCodes.IsPlatformAdmin, await refused.ErrorCodeAsync());
    }

    /// <summary>The venue has not said whether the money came: nobody could tell them after.</summary>
    [Fact]
    public async Task Not_while_the_venue_has_not_answered_about_their_payment()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        Assert.Equal(
            HttpStatusCode.OK, (await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null)).StatusCode);

        var refused = await DeleteAsync(booker, VenueScenario.Password);

        Assert.Equal(AccountDeletionErrorCodes.HasMoneyPending, await refused.ErrorCodeAsync());
    }

    /// <summary>
    /// Played, but still inside the day a venue may correct it — which could leave money owed
    /// back to somebody nobody can reach (US-13, VenueDecisions.CorrectionWindow).
    /// </summary>
    [Fact]
    public async Task Not_while_a_venue_may_still_correct_what_they_played()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromHours(-2));

        var refused = await DeleteAsync(booker, VenueScenario.Password);

        Assert.Equal(AccountDeletionErrorCodes.HasUpcomingBookings, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_hold_that_ran_out_does_not_keep_anybody()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var held = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[0], 18));
        await scenario.LapseHoldAsync(held.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(booker, VenueScenario.Password)).StatusCode);
    }

    /// <summary>Guessing the password here locks the account like the sign-in page does.</summary>
    [Fact]
    public async Task Guessing_the_password_locks_it()
    {
        var client = await scenario.SignedInClientAsync();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await DeleteAsync(client, "NotThePassword1");
        }

        var locked = await DeleteAsync(client, VenueScenario.Password);

        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        Assert.Equal(AccountDeletionErrorCodes.LockedOut, await locked.ErrorCodeAsync());
    }

    /// <summary>
    /// Another device still holds a session until it revalidates. Whatever it tries to write that
    /// would need the person again is refused by the row in the meantime (S-15).
    /// </summary>
    [Fact]
    public async Task A_session_left_open_elsewhere_cannot_book_or_open_a_venue()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        using var slow = api.Api.WithWebHostBuilder(builder =>
            builder.UseSetting("App:SessionRevalidationSeconds", "3600"));
        var email = scenario.NewEmail();
        var elsewhere = slow.CreateClient();
        Assert.Equal(
            HttpStatusCode.Created,
            (await elsewhere.PostAsJsonAsync(
                "/api/auth/register",
                new RegisterRequest(
                    email, VenueScenario.Password, ApiFactory.PrivacyPolicyVersion,
                    SupportedLanguages.Thai, null))).StatusCode);
        await scenario.ConfirmEmailAsync(email);
        await elsewhere.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, VenueScenario.Password));
        var here = slow.CreateClient();
        await here.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, VenueScenario.Password));

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(here, VenueScenario.Password)).StatusCode);

        var booking = await elsewhere.PostAsJsonAsync(
            "/api/bookings",
            new CreateBookingRequest(
                venue.Id, [new BookingSlotRequest(courts[0], VenueScenario.Today.AddDays(1), 18)]));
        var applying = await elsewhere.PostAsJsonAsync(
            "/api/venues", VenueScenario.Application(scenario.NewCode()));

        Assert.NotEqual(HttpStatusCode.Created, booking.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, applying.StatusCode);
        Assert.Equal(AccountGate.Closed, await applying.ErrorCodeAsync());
    }

    /// <summary>The booker's own moves in a complaint's history name nobody either (PRD 8).</summary>
    [Fact]
    public async Task A_complaint_history_names_nobody_for_a_forgotten_booker()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);
        await scenario.PlayOutAsync(booking.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(booker, VenueScenario.Password)).StatusCode);

        var complaint = await VenueScenario.ReadAsync<ComplaintResponse>(
            await admin.PostAsJsonAsync(
                "/api/admin/complaints",
                new OpenComplaintRequest(booking.Id.ToString(), "ทดสอบ", "Email")),
            HttpStatusCode.Created);

        Assert.Null(complaint.Booking.BookerEmail);
        Assert.DoesNotContain(
            complaint.Booking.History, move => move.ChangedByEmail?.Contains("deleted") == true);
    }

    [Fact]
    public async Task A_forgotten_account_is_not_found_by_search()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (client, _) = await scenario.SignedInClientWithEmailAsync();
        await DeleteAsync(client, VenueScenario.Password);

        var found = await VenueScenario.ReadAsync<AdminUserResponse[]>(
            await admin.GetAsync("/api/admin/users?q=deleted-"));

        Assert.Empty(found);
    }

    /// <summary>An accepted invitation was the last copy of the address (PDPA, PRD 8).</summary>
    [Fact]
    public async Task The_invitation_that_brought_them_in_no_longer_holds_the_address()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var (staff, email) = await scenario.SignedInClientWithEmailAsync();
        await scenario.InviteAndAcceptAsync(owner, staff, venue.Id, email);

        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(staff, VenueScenario.Password)).StatusCode);

        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await database.VenueInvitations.AnyAsync(
            invitation => invitation.NormalizedEmail == email.ToUpperInvariant()));
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
