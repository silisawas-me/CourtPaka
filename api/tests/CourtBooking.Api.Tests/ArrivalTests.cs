using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>
/// Who is coming and who is here (PRD US-24). It is not the booking's status: a booking that is
/// paid for says nothing about somebody walking through the door, which is the whole point.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ArrivalTests(ApiTestFixture api)
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task A_booking_starts_with_nobody_having_said_anything()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);

        var row = await DayRowAsync(owner, venue.Id, booking.Id);

        Assert.Equal(nameof(BookingArrival.Unconfirmed), row.Arrival);
        Assert.Null(row.ArrivedAt);
        Assert.True(row.Can.ConfirmArrival);
        // The hours are hours away, so nobody is standing at the desk yet.
        Assert.False(row.Can.CheckIn);
    }

    [Fact]
    public async Task The_counter_writes_down_that_they_called_to_say_they_are_coming()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);

        var said = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/confirm-arrival", null));

        Assert.Equal(nameof(BookingArrival.Confirmed), said.Arrival);
        Assert.False(said.Can.ConfirmArrival);

        // Said once. Saying it again is not a second thing that happened.
        var again = await owner.PostAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/confirm-arrival", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(BookingErrorCodes.ArrivalNotAllowed, await again.ErrorCodeAsync());

        var history = await ArrivalHistoryAsync(booking.Id);
        var step = Assert.Single(history);
        Assert.Equal((BookingArrival.Unconfirmed, BookingArrival.Confirmed), (step.From, step.To));
        Assert.NotNull(step.ChangedByUserId);
    }

    /// <summary>Checking in is for somebody who is here, which means the hours are now.</summary>
    [Fact]
    public async Task Somebody_is_checked_in_once_their_hour_is_close_and_not_before()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);

        var tooEarly = await owner.PostAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/check-in", null);
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        Assert.Equal(BookingErrorCodes.ArrivalNotAllowed, await tooEarly.ErrorCodeAsync());

        // Twenty minutes before their hour: the court before theirs is finishing and they are here.
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(20));

        var here = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/check-in", null));

        Assert.Equal(nameof(BookingArrival.Arrived), here.Arrival);
        Assert.NotNull(here.ArrivedAt);
        Assert.False(here.Can.CheckIn);
        Assert.False(here.Can.ConfirmArrival);
    }

    /// <summary>
    /// The steps only go forward. Checking somebody in without anybody having said they were
    /// coming is normal — they simply turned up — and the history says exactly that.
    /// </summary>
    [Fact]
    public async Task Turning_up_unannounced_is_one_step_from_where_it_was()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 19);
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(5));

        await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/check-in", null);

        var step = Assert.Single(await ArrivalHistoryAsync(booking.Id));
        Assert.Equal((BookingArrival.Unconfirmed, BookingArrival.Arrived), (step.From, step.To));

        // And nothing walks it back.
        var backwards = await owner.PostAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/confirm-arrival", null);
        Assert.Equal(HttpStatusCode.Conflict, backwards.StatusCode);
    }

    /// <summary>A booking nobody has paid for is not one anybody is let in on (PRD US-24).</summary>
    [Fact]
    public async Task A_booking_still_waiting_on_its_slip_cannot_be_checked_in()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(5));

        var refused = await owner.PostAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/check-in", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.ArrivalNotAllowed, await refused.ErrorCodeAsync());
        // But the venue may still write down that they rang to say they are on their way.
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PostAsync(
                $"/api/venues/{venue.Id}/bookings/{booking.Id}/confirm-arrival", null)).StatusCode);
    }

    /// <summary>
    /// How long a venue waits before somebody has not come is the venue's own (PRD US-24). The
    /// day's list says when that moment is, and the no-show door opens exactly then.
    /// </summary>
    [Fact]
    public async Task A_venue_that_waits_longer_may_not_write_anybody_off_sooner()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.PutAsJsonAsync(
                $"/api/venues/{venue.Id}/grace", new GraceRequest(40))).StatusCode);

        // And the venue says it back, so the settings page starts from what is set (owner-complete 2c).
        var mine = await VenueScenario.ReadAsync<VenueResponse[]>(await owner.GetAsync("/api/venues/mine"));
        Assert.Equal(40, Assert.Single(mine, one => one.Id == venue.Id).GraceMinutes);

        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        // Twenty minutes past the hour: late by the usual fifteen, not by this venue's forty.
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-20));

        // The day the hours actually landed on, not today: twenty minutes before now is
        // yesterday for the hour after Thai midnight, and the list is by the venue's day.
        var row = await DayRowAsync(owner, venue.Id, booking.Id, await PlayDayAsync(booking.Id));
        Assert.False(row.Can.NoShow);
        // The list says when the wait is over, so the screen can count it down without a rule of
        // its own: forty minutes after the hour this venue sold.
        Assert.Equal(40, (row.GraceEndsAt - await PlayStartsAtAsync(booking.Id)).TotalMinutes, 1);

        var tooSoon = await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/no-show", null);
        Assert.Equal(HttpStatusCode.Conflict, tooSoon.StatusCode);
        Assert.Equal(BookingErrorCodes.NotYetLateEnough, await tooSoon.ErrorCodeAsync());

        // Three quarters of an hour past, and the venue's own wait is over.
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-45));
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/no-show", null)).StatusCode);
    }

    [Fact]
    public async Task The_wait_a_venue_sets_has_to_be_a_wait_somebody_could_keep()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        foreach (var minutes in new[] { -1, VenueDecisions.MaxGraceMinutes + 1 })
        {
            var refused = await owner.PutAsJsonAsync(
                $"/api/venues/{venue.Id}/grace", new GraceRequest(minutes));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(VenueErrorCodes.InvalidGrace, await refused.ErrorCodeAsync());
        }
    }

    /// <summary>Staff without ManageBookings do not touch what the counter writes down.</summary>
    [Fact]
    public async Task A_member_of_staff_without_the_permission_cannot_check_anybody_in()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var stranger = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(Venues.VenuePermissions.VerifySlip));
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(5));

        var refused = await stranger.PostAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/check-in", null);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    /// <summary>
    /// When the hours this booking holds actually begin. Read from the database rather than from
    /// the row's date and hour, because a test that moves a booking to "twenty minutes ago" lands
    /// it between two hours, and the row rounds.
    /// </summary>
    private async Task<DateTimeOffset> PlayStartsAtAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.BookingSlots
            .Where(slot => slot.BookingId == bookingId)
            .MinAsync(slot => slot.StartsAt);
    }

    /// <summary>
    /// One booking as the counter's day list draws it. The date is asked for, because a booking
    /// whose hours were moved to now is on today's list, not on the one it was sold for.
    /// </summary>
    /// <summary>The venue's own day the booking's first hour falls on (PRD BR-10).</summary>
    private async Task<DateOnly> PlayDayAsync(Guid bookingId) =>
        PlatformRequirements.BangkokDateAndHour(await PlayStartsAtAsync(bookingId)).Date;

    private static async Task<VenueBookingResponse> DayRowAsync(
        HttpClient client, Guid venueId, Guid bookingId, DateOnly? date = null)
    {
        var day = await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await client.GetAsync(
                $"/api/venues/{venueId}/bookings?date={date ?? VenueScenario.Today.AddDays(1):yyyy-MM-dd}"));
        return day.Single(booking => booking.BookingId == bookingId);
    }

    private async Task<BookingArrivalChange[]> ArrivalHistoryAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.BookingArrivalChanges
            .AsNoTracking()
            .Where(change => change.BookingId == bookingId)
            .OrderBy(change => change.ChangedAt)
            .ThenBy(change => change.Id)
            .ToArrayAsync();
    }
}
