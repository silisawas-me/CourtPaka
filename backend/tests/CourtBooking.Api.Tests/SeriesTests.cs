using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Jobs;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>
/// The group that comes every week (PRD US-30). What is tested here is that an arrangement makes
/// ordinary bookings on the terms of the day it makes them, never takes an hour somebody already
/// has, and says so when it could not.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SeriesTests(ApiTestFixture api)
{
    private readonly VenueScenario scenario = new(api);

    /// <summary>
    /// Far enough ahead that the other suites are not booking into it, and inside the window a
    /// week may be made in.
    /// </summary>
    private static readonly DateOnly Week = VenueScenario.Today.AddDays(26);

    [Fact]
    public async Task A_week_inside_the_window_becomes_an_ordinary_booking_nobody_has_paid_for()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        var agreed = await AgreeAsync(owner, venue.Id, courts[0]);
        Assert.Equal("Running", agreed.State);
        Assert.Equal(0, agreed.Booked);

        await FillAsync();

        var booking = await OnlyWeekAsync(agreed.SeriesId);

        // A counter booking in every way but the money: the court is theirs, and the desk takes
        // what is owed when they turn up (PRD US-26, US-30).
        Assert.Equal(BookingChannel.Staff, booking.Channel);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(PaymentState.NotReceived, booking.PaymentState);
        Assert.Null(booking.PaidAtCounter);
        Assert.Null(booking.BookerUserId);
        Assert.Equal("ก๊วนอังคาร", booking.CustomerName);
        Assert.Equal(400m, booking.TotalBaht);
        Assert.Equal(2, booking.Slots.Count);
        Assert.All(booking.Slots, slot => Assert.Equal(courts[0], slot.CourtId));

        // And the day's list has it, which is the only place the counter looks.
        var day = await DayAsync(owner, venue.Id, Week);
        var row = Assert.Single(day, one => one.BookingId == booking.Id);
        Assert.Equal(400m, row.ToPayBaht);
        Assert.True(row.Can.TakeMoney);
    }

    /// <summary>
    /// Nobody made this week: the clock did. Saying a person did would go on attributing new
    /// sales to whoever typed the arrangement in, long after they may have left the venue
    /// (PRD 6.1 — a null actor is the system).
    /// </summary>
    [Fact]
    public async Task The_week_is_recorded_as_something_the_clock_did()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var agreed = await AgreeAsync(owner, venue.Id, courts[0]);
        await FillAsync();

        var booking = await OnlyWeekAsync(agreed.SeriesId);
        var first = Assert.Single(await scenario.HistoryAsync(booking.Id));

        Assert.Null(first.From);
        Assert.Equal(BookingStatus.Confirmed, first.To);
        Assert.Null(first.ChangedByUserId);

        // Who decided it is still readable: the booking points at the arrangement, and the
        // arrangement says who agreed it.
        Assert.Equal(agreed.SeriesId, booking.SeriesId);
    }

    /// <summary>
    /// A week whose hours are already over is not made. The counter may sell an hour that has
    /// started because somebody is standing there (PRD US-13); nobody is standing in front of a
    /// job, and a booking for an hour nobody can play is a booking somebody has to undo.
    /// </summary>
    [Fact]
    public async Task A_week_that_is_already_over_is_not_booked()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        // Today, at the first hour the venue opens — which has passed by any hour this suite is
        // likely to run at, and if it has not, the week is simply still to come and is made.
        var today = VenueScenario.Today;
        var agreed = await VenueScenario.ReadAsync<BookingSeriesResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/series",
                new BookingSeriesRequest(
                    courts[0], today.DayOfWeek.ToString(), 6, 7, "ก๊วนเช้า", null, today, today)),
            HttpStatusCode.Created);

        await FillAsync();

        var over = PlatformRequirements.BangkokHour(today, 6) <= DateTimeOffset.UtcNow;
        var standing = await ListAsync(owner, venue.Id);
        var one = Assert.Single(standing, row => row.SeriesId == agreed.SeriesId);

        if (over)
        {
            // Nothing made, and nothing reported as missed either: nobody could have had it.
            Assert.Equal(0, one.Booked);
            Assert.Empty(one.Missed);
        }
        else
        {
            Assert.Equal(1, one.Booked);
        }
    }

    /// <summary>
    /// The price is the price on the day the week is made, not the day the arrangement was agreed
    /// (PRD BR-05). A venue that puts its evenings up gets the new rate from the next week it
    /// makes, exactly as somebody booking that week by hand would pay.
    /// </summary>
    [Fact]
    public async Task A_week_is_priced_when_it_is_made_and_not_when_it_was_agreed()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var agreed = await AgreeAsync(owner, venue.Id, courts[0]);

        await VenueScenario.SetPricesAsync(owner, venue.Id, VenueScenario.AllWeek(6, 22, 500m));
        await FillAsync();

        var booking = await OnlyWeekAsync(agreed.SeriesId);
        Assert.Equal(1_000m, booking.TotalBaht);
        Assert.All(booking.Slots, slot => Assert.Equal(500m, slot.BahtPerHour));
    }

    /// <summary>
    /// An hour somebody else has is not taken from them (PRD BR-04). The week is reported back to
    /// the venue instead, with why — the same answer closing a court gives when a booking is in
    /// the way (PRD US-11), because what to do about it is the venue's call and not the job's.
    /// </summary>
    [Fact]
    public async Task An_hour_somebody_already_has_is_left_alone_and_the_week_is_reported()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        // The counter sells the group's first hour to somebody standing there.
        Assert.Equal(
            HttpStatusCode.Created,
            (await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings",
                new CounterBookingRequest(
                    [new BookingSlotRequest(courts[0], Week, 18)],
                    "คนเดินเข้ามา",
                    null,
                    nameof(CounterPayment.Cash)))).StatusCode);

        var agreed = await AgreeAsync(owner, venue.Id, courts[0]);
        await FillAsync();

        Assert.Empty(await WeeksAsync(agreed.SeriesId));

        var standing = await ListAsync(owner, venue.Id);
        var one = Assert.Single(standing, row => row.SeriesId == agreed.SeriesId);
        var missed = Assert.Single(one.Missed);
        Assert.Equal(Week, missed.Date);
        Assert.Equal(BookingErrorCodes.SlotJustTaken, missed.Refusal);
        Assert.Equal(0, one.Booked);
    }

    /// <summary>
    /// A week that could not be had is not tried again on the next sweep. The venue has been told
    /// about it and may have promised the hour elsewhere; creeping in behind them later would be
    /// the job deciding something the venue was handed.
    /// </summary>
    [Fact]
    public async Task A_week_is_answered_once_however_many_sweeps_there_are()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var agreed = await AgreeAsync(owner, venue.Id, courts[0]);

        await FillAsync();
        await FillAsync();
        await FillAsync();

        Assert.Single(await WeeksAsync(agreed.SeriesId));
    }

    /// <summary>
    /// Hours the venue is not open for are a week it cannot have, and the arrangement says so
    /// rather than quietly making nothing (PRD US-30).
    /// </summary>
    [Fact]
    public async Task A_week_outside_the_opening_hours_is_reported_rather_than_skipped()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var agreed = await AgreeAsync(owner, venue.Id, courts[0], fromHour: 23, untilHour: 24);

        await FillAsync();

        var standing = await ListAsync(owner, venue.Id);
        var one = Assert.Single(standing, row => row.SeriesId == agreed.SeriesId);
        var missed = Assert.Single(one.Missed);
        Assert.Equal(BookingErrorCodes.HourNotAvailable, missed.Refusal);
    }

    /// <summary>
    /// Stopping an arrangement cancels the weeks still to come, one at a time and each under its
    /// own terms (PRD US-30, BR-05). The row stays with when and why, because what a venue stood
    /// a group down over is the first thing asked about afterwards.
    /// </summary>
    [Fact]
    public async Task Stopping_it_cancels_the_weeks_still_to_come_and_keeps_the_reason()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var agreed = await AgreeAsync(owner, venue.Id, courts[0]);
        await FillAsync();

        var booking = await OnlyWeekAsync(agreed.SeriesId);

        var stopped = await VenueScenario.ReadAsync<BookingSeriesStoppedResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/series/{agreed.SeriesId}/stop",
                new BookingSeriesStopRequest("ก๊วนเลิกเล่น")));

        Assert.Equal(1, stopped.Cancelled);
        Assert.Equal(0, stopped.Left);
        Assert.Equal("Ended", stopped.Series.State);
        Assert.Equal("ก๊วนเลิกเล่น", stopped.Series.EndReason);
        Assert.NotNull(stopped.Series.EndedAt);

        var after = await scenario.StoredBookingAsync(booking.Id);
        Assert.Equal(BookingStatus.Cancelled, after.Status);

        // The hours are on sale again, and the cancellation says who ended it and why (PRD 6.1).
        Assert.False(await scenario.HoldsItsHoursAsync(booking.Id));
        var history = await scenario.HistoryAsync(booking.Id);
        Assert.Contains(
            history,
            move => move.To == BookingStatus.Cancelled
                && move.Cause == CancellationReason.VenueInitiated);

        // And nothing more is made for it.
        await FillAsync();
        Assert.Single(await WeeksAsync(agreed.SeriesId));
    }

    [Fact]
    public async Task Stopping_one_that_has_already_stopped_is_refused()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var agreed = await AgreeAsync(owner, venue.Id, courts[0]);

        await StopAsync(owner, venue.Id, agreed.SeriesId);

        var again = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/series/{agreed.SeriesId}/stop",
            new BookingSeriesStopRequest(null));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(SeriesErrorCodes.AlreadyEnded, await CodeAsync(again));
    }

    /// <summary>
    /// Changing an arrangement from a date writes a second one that took over, rather than editing
    /// the first (PRD US-30). The weeks already played were played on the old terms, and a row
    /// rewritten is a row that no longer says so.
    /// </summary>
    [Fact]
    public async Task Changing_it_ends_the_old_arrangement_and_starts_one_that_took_over()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var agreed = await AgreeAsync(owner, venue.Id, courts[0]);
        await FillAsync();

        var before = await OnlyWeekAsync(agreed.SeriesId);

        var changed = await VenueScenario.ReadAsync<BookingSeriesStoppedResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/series/{agreed.SeriesId}/change",
                Request(courts[1], fromHour: 19, untilHour: 21)));

        Assert.NotEqual(agreed.SeriesId, changed.Series.SeriesId);
        Assert.Equal(courts[1], changed.Series.CourtId);
        Assert.Equal(1, changed.Cancelled);

        // The old one is over, and the week it had made is given back.
        var standing = await ListAsync(owner, venue.Id);
        Assert.Equal("Ended", Assert.Single(standing, row => row.SeriesId == agreed.SeriesId).State);
        Assert.Equal(
            BookingStatus.Cancelled,
            (await scenario.StoredBookingAsync(before.Id)).Status);

        // The new one makes the same week on the new court and the new hours.
        await FillAsync();
        var after = await OnlyWeekAsync(changed.Series.SeriesId);
        Assert.All(after.Slots, slot => Assert.Equal(courts[1], slot.CourtId));
        Assert.Equal(
            [19, 20],
            after.Slots
                .Select(slot => PlatformRequirements.BangkokDateAndHour(slot.StartsAt).Hour)
                .OrderBy(hour => hour)
                .ToArray());
    }

    [Theory]
    [InlineData(20, 18, SeriesErrorCodes.NotAWindow)]
    [InlineData(18, 18, SeriesErrorCodes.NotAWindow)]
    [InlineData(18, 25, SeriesErrorCodes.NotAWindow)]
    public async Task Hours_that_are_not_a_window_are_refused(int from, int until, string code)
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/series",
            Request(courts[0], fromHour: from, untilHour: until));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(code, await CodeAsync(refused));
    }

    [Fact]
    public async Task A_weekday_that_is_not_one_of_the_seven_is_refused()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        // "2" parses as Tuesday and "Monday,Tuesday" ors into a third day; neither is a name.
        foreach (var day in new[] { "2", "Monday,Tuesday", "วันอังคาร" })
        {
            var refused = await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/series",
                Request(courts[0]) with { Day = day });

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(SeriesErrorCodes.DayUnknown, await CodeAsync(refused));
        }
    }

    [Fact]
    public async Task A_first_week_in_the_past_is_refused()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/series",
            Request(courts[0]) with { StartsOn = VenueScenario.Today.AddDays(-7), UntilOn = null });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(SeriesErrorCodes.StartsInThePast, await CodeAsync(refused));
    }

    [Fact]
    public async Task A_court_of_another_venue_is_not_a_court_of_this_one()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var (_, _, elsewhere) = await scenario.BookableVenueAsync();

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/series", Request(elsewhere[0]));

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(SeriesErrorCodes.CourtUnknown, await CodeAsync(refused));
    }

    /// <summary>
    /// An arrangement carries the group's name and phone number, so every door of it — the reading
    /// included — is behind the permission that door belongs to (PDPA, as PRD US-13).
    /// </summary>
    [Fact]
    public async Task Somebody_without_the_permission_cannot_even_read_them()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.VerifySlip));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await staff.GetAsync($"/api/venues/{venue.Id}/series")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await staff.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/series", Request(courts[0]))).StatusCode);
    }

    /// <summary>
    /// A suspension stops a venue selling, and a week of a standing arrangement is a sale. What it
    /// does not stop is the venue standing a group down: an arrangement it can no longer honour is
    /// one it has to be able to end (PRD US-20).
    /// </summary>
    [Fact]
    public async Task A_suspended_venue_cannot_agree_one_but_can_still_stop_one()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var agreed = await AgreeAsync(owner, venue.Id, courts[0]);

        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/series", Request(courts[0]))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync($"/api/venues/{venue.Id}/series")).StatusCode);

        // And nothing is made for it while the venue is stopped.
        await FillAsync();
        Assert.Empty(await WeeksAsync(agreed.SeriesId));

        await StopAsync(owner, venue.Id, agreed.SeriesId);
    }

    /// <summary>
    /// Arrangements are kept for ever, so the list has to stop somewhere. What it never stops
    /// showing is anything still running (PRD US-30).
    /// </summary>
    [Fact]
    public async Task The_list_keeps_everything_running_and_only_the_last_few_that_stopped()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();

        for (var hour = 6; hour < 6 + Series.EndedShown + 2; hour++)
        {
            var one = await AgreeAsync(owner, venue.Id, courts[0], hour, hour + 1);
            await StopAsync(owner, venue.Id, one.SeriesId);
        }

        var stillRunning = await AgreeAsync(owner, venue.Id, courts[0], 20, 21);

        var standing = await ListAsync(owner, venue.Id);
        Assert.Equal(Series.EndedShown, standing.Count(one => one.State == "Ended"));
        Assert.Single(standing, one => one.SeriesId == stillRunning.SeriesId);
    }

    /// <summary>
    /// A second group on the same court, the same day, at the same hours could never have a week
    /// of it — every one would find the first already there, and a week written off is written
    /// off for good (PRD US-30).
    /// </summary>
    [Fact]
    public async Task A_second_group_on_the_same_court_day_and_hours_is_refused()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        await AgreeAsync(owner, venue.Id, courts[0]);

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/series", Request(courts[0]));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(SeriesErrorCodes.AlreadyStanding, await CodeAsync(refused));

        // The same hours on another court are a different group, and are allowed.
        var second = await VenueScenario.ReadAsync<CourtResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/courts", new CreateCourtRequest("Court 2")),
            HttpStatusCode.Created);

        await AgreeAsync(owner, venue.Id, second.Id);
    }

    /// <summary>
    /// Changing an arrangement is not a second one: it says the same thing about the same court,
    /// and what it takes over from is about to stop (PRD US-30).
    /// </summary>
    [Fact]
    public async Task Changing_one_is_not_refused_for_looking_like_itself()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var agreed = await AgreeAsync(owner, venue.Id, courts[0]);

        var changed = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/series/{agreed.SeriesId}/change",
            Request(courts[0]) with { CustomerPhone = "0899999999" });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
    }

    /// <summary>
    /// The arrangement being changed stops the moment the change is agreed, so a handover
    /// further ahead than the weeks can be booked would leave the weeks in between with nobody
    /// making them (PRD US-30, S-04).
    /// </summary>
    [Fact]
    public async Task A_change_that_starts_beyond_the_booking_window_is_refused()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var agreed = await AgreeAsync(owner, venue.Id, courts[0]);

        var faraway = VenueScenario.Today.AddDays(Series.FillWithinDays + 7);
        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/series/{agreed.SeriesId}/change",
            Request(courts[0]) with { StartsOn = faraway, UntilOn = faraway });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(SeriesErrorCodes.ChangeTooFarAhead, await CodeAsync(refused));

        // And the arrangement it would have replaced is untouched.
        var standing = await ListAsync(owner, venue.Id);
        Assert.Equal("Running", Assert.Single(standing, one => one.SeriesId == agreed.SeriesId).State);
    }

    /// <summary>
    /// A change that keeps the court and the hours has to give the old arrangement's weeks back
    /// before it asks for them, or it finds its own hours taken and writes every week off.
    /// </summary>
    [Fact]
    public async Task A_change_that_keeps_the_hours_takes_the_weeks_over_rather_than_missing_them()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var agreed = await AgreeAsync(owner, venue.Id, courts[0]);
        await FillAsync();
        Assert.Single(await WeeksAsync(agreed.SeriesId));

        var changed = await VenueScenario.ReadAsync<BookingSeriesStoppedResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/series/{agreed.SeriesId}/change",
                Request(courts[0]) with { CustomerName = "ก๊วนชื่อใหม่" }));

        Assert.Equal(1, changed.Cancelled);

        await FillAsync();

        var taken = await OnlyWeekAsync(changed.Series.SeriesId);
        Assert.Equal("ก๊วนชื่อใหม่", taken.CustomerName);
        Assert.Empty((await ListAsync(owner, venue.Id))
            .Single(one => one.SeriesId == changed.Series.SeriesId).Missed);
    }

    /// <summary>
    /// The number beside an arrangement is the weeks it still has. Leaving the cancelled ones in
    /// it would make a stop look as though it had done nothing.
    /// </summary>
    [Fact]
    public async Task Stopping_it_leaves_no_weeks_counted_against_it()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var agreed = await AgreeAsync(owner, venue.Id, courts[0]);
        await FillAsync();

        Assert.Equal(
            1,
            (await ListAsync(owner, venue.Id)).Single(one => one.SeriesId == agreed.SeriesId).Booked);

        await StopAsync(owner, venue.Id, agreed.SeriesId);

        Assert.Equal(
            0,
            (await ListAsync(owner, venue.Id)).Single(one => one.SeriesId == agreed.SeriesId).Booked);
    }

    private static BookingSeriesRequest Request(
        Guid courtId,
        int fromHour = 18,
        int untilHour = 20) =>
        new(
            courtId,
            Week.DayOfWeek.ToString(),
            fromHour,
            untilHour,
            "ก๊วนอังคาร",
            "0812345678",
            Week,
            // One week only, so the suite does not fill a month of somebody else's floor.
            Week);

    private async Task<BookingSeriesResponse> AgreeAsync(
        HttpClient owner,
        Guid venueId,
        Guid courtId,
        int fromHour = 18,
        int untilHour = 20) =>
        await VenueScenario.ReadAsync<BookingSeriesResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/series", Request(courtId, fromHour, untilHour)),
            HttpStatusCode.Created);

    private static async Task StopAsync(HttpClient owner, Guid venueId, Guid seriesId) =>
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/series/{seriesId}/stop",
                new BookingSeriesStopRequest(null))).StatusCode);

    private static async Task<BookingSeriesResponse[]> ListAsync(HttpClient owner, Guid venueId) =>
        await VenueScenario.ReadAsync<BookingSeriesResponse[]>(
            await owner.GetAsync($"/api/venues/{venueId}/series"));

    private static async Task<VenueBookingResponse[]> DayAsync(
        HttpClient owner,
        Guid venueId,
        DateOnly date) =>
        await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await owner.GetAsync($"/api/venues/{venueId}/bookings?date={date:yyyy-MM-dd}"));

    private async Task FillAsync()
    {
        using var scope = api.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SeriesBookings>()
            .WorkAsync(CancellationToken.None);
    }

    private async Task<Booking[]> WeeksAsync(Guid seriesId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await database.Bookings
            .AsNoTracking()
            .Include(booking => booking.Slots)
            .Where(booking => booking.SeriesId == seriesId)
            .ToArrayAsync();
    }

    private async Task<Booking> OnlyWeekAsync(Guid seriesId) =>
        Assert.Single(await WeeksAsync(seriesId));

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemBody>())?.Code;

    private sealed record ProblemBody(string? Code);
}
