using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Jobs;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>The work nobody asks for: PRD 9.2, BR-02, S-22, US-17 S-23.</summary>
public sealed class CaretakerTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    private static readonly DateOnly Tomorrow = VenueScenario.Today.AddDays(1);

    /// <summary>
    /// The bug this exists for. Nothing reads the hours a lapsed hold is sitting on, so before
    /// the caretaker they stayed taken — and the booker stayed locked out of booking again.
    /// </summary>
    [Fact]
    public async Task A_hold_that_ran_out_on_hours_nobody_reads_is_given_back_anyway()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));
        await scenario.LapseHoldAsync(booking.Id);

        await SweepAsync();

        Assert.Equal(BookingStatus.Expired, await StatusAsync(booking.Id));
        Assert.False(await AnyActiveSlotAsync(booking.Id));

        // And the history says the clock did it, not a person (PRD 6.1).
        var expiry = Assert.Single(
            await scenario.HistoryAsync(booking.Id), move => move.To == BookingStatus.Expired);
        Assert.Null(expiry.ChangedByUserId);
    }

    [Fact]
    public async Task And_its_booker_can_hold_hours_again()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));
        await scenario.LapseHoldAsync(booking.Id);

        await SweepAsync();

        // The one-hold-at-a-time rule counts rows that still say Held, and cannot ask the time
        // (PRD S-22). Nothing here read those hours, so only the sweep can have freed them.
        var again = await booker.PostAsJsonAsync(
            "/api/bookings",
            new CreateBookingRequest(
                venue.Id, [new BookingSlotRequest(courts[0], Tomorrow, 19)]));

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task A_hold_still_in_time_is_left_alone()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));

        await SweepAsync();

        Assert.Equal(BookingStatus.Held, await StatusAsync(booking.Id));
        Assert.True(await AnyActiveSlotAsync(booking.Id));
    }

    /// <summary>
    /// A slip nobody has looked at, with the hours about to be played (PRD US-17, S-23). Told to
    /// the owner, and told once.
    /// </summary>
    [Fact]
    public async Task A_slip_still_waiting_when_the_court_is_about_to_be_played_reminds_the_owner()
    {
        var (owner, venue, booking) = await WaitingSoonAsync();
        var ownerAddress = await OwnerEmailAsync(venue.Id);
        var before = CountTo(ownerAddress);

        await SweepAsync();

        Assert.Equal(before + 1, CountTo(ownerAddress));
        Assert.Contains(
            "about to be played",
            api.Emails.LastTo(ownerAddress).Subject,
            StringComparison.OrdinalIgnoreCase);

        // And again, and nothing more is sent: the row was claimed before the message went out.
        await SweepAsync();
        Assert.Equal(before + 1, CountTo(ownerAddress));

        Assert.NotNull(owner);
        Assert.NotNull(booking);
    }

    [Fact]
    public async Task Staff_who_only_check_slips_are_not_the_ones_reminded()
    {
        var (owner, venue, _) = await WaitingSoonAsync();
        var (checker, checkerAddress) = await scenario.SignedInClientWithEmailAsync();
        await scenario.InviteAndAcceptAsync(
            owner, checker, venue.Id, checkerAddress, [nameof(VenuePermissions.VerifySlip)]);

        var before = CountTo(checkerAddress);
        await SweepAsync();

        // The queue's own "a slip arrived" message is theirs; this last call before the hours are
        // played is the owner's (PRD US-17).
        Assert.Equal(before, CountTo(checkerAddress));
    }

    [Fact]
    public async Task A_slip_whose_hours_are_further_off_is_not_reminded_about_yet()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var ownerAddress = await OwnerEmailAsync(venue.Id);
        var before = CountTo(ownerAddress);

        await SweepAsync();

        Assert.Equal(before, CountTo(ownerAddress));
        Assert.NotNull(booker);
        Assert.NotNull(booking);
    }

    [Fact]
    public async Task A_slip_already_looked_at_is_not_reminded_about()
    {
        var (owner, venue, booking) = await WaitingSoonAsync();
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        var ownerAddress = await OwnerEmailAsync(venue.Id);
        var before = CountTo(ownerAddress);

        await SweepAsync();

        Assert.Equal(before, CountTo(ownerAddress));
    }

    /// <summary>
    /// How much has been written to one address. By address rather than by counting everything,
    /// because the sender is shared by the whole run and classes run alongside each other.
    /// </summary>
    private int CountTo(string email) => api.Emails.To(email).Count;

    private async Task<string> OwnerEmailAsync(Guid venueId) =>
        (await scenario.MemberEmailsAsync(venueId)).Values.Single();

    /// <summary>A booking waiting on its slip whose first hour starts inside the half hour.</summary>
    private async Task<(HttpClient Owner, VenueResponse Venue, BookingResponse Booking)>
        WaitingSoonAsync()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        // Moved rather than booked at that hour: the hour a venue is open at is not negotiable,
        // and what is being tested is how close the start is, not which hour it is.
        await StartInAsync(booking.Id, TimeSpan.FromMinutes(20));

        return (owner, venue, booking);
    }

    private async Task StartInAsync(Guid bookingId, TimeSpan from)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        await database.BookingSlots
            .Where(slot => slot.BookingId == bookingId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(slot => slot.StartsAt, now + from)
                .SetProperty(slot => slot.EndsAt, now + from + TimeSpan.FromHours(1)));
    }

    /// <summary>One pass of the caretaker's chores, run the way the timer would run them.</summary>
    private async Task SweepAsync()
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<VenueNotifications>();
        var now = DateTimeOffset.UtcNow;

        await BookedSlots.ReleaseAllLapsedAsync(database, now, CancellationToken.None);

        var waiting = await WaitingSlips.AboutToBePlayedAsync(
            database, now, CancellationToken.None);

        foreach (var reminder in waiting)
        {
            await notifications.SlipStillWaitingAsync(reminder.VenueId, reminder.BookingId);
        }
    }

    private async Task<BookingStatus> StatusAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.Bookings
            .AsNoTracking()
            .Where(booking => booking.Id == bookingId)
            .Select(booking => booking.Status)
            .SingleAsync(CancellationToken.None);
    }

    private async Task<bool> AnyActiveSlotAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.BookingSlots
            .AsNoTracking()
            .AnyAsync(
                slot => slot.BookingId == bookingId && slot.IsActive,
                CancellationToken.None);
    }
}
