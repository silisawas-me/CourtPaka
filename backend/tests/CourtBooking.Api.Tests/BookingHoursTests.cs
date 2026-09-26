using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>
/// One more hour, and a different court (PRD US-29). The two things an evening asks for while it
/// is being played, neither of which is a new booking.
/// </summary>
public sealed class BookingHoursTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    private static readonly DateOnly Tomorrow = VenueScenario.Today.AddDays(1);

    [Fact]
    public async Task An_hour_is_added_to_the_booking_that_is_already_being_played()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);

        var extended = await Extend(owner, venue.Id, booking.Id);

        Assert.Equal(
            [(courts[0], 18), (courts[0], 19)],
            extended.Slots.Select(slot => (slot.CourtId, slot.Hour)).Order());

        // The same booking, not a second one: the status is where it was and the price grew.
        Assert.Equal(nameof(BookingStatus.Confirmed), extended.Status);
        Assert.Equal(400m, extended.TotalBaht);
        Assert.Single(await scenario.HistoryAsync(booking.Id), change =>
            change.To == BookingStatus.Confirmed);
    }

    /// <summary>
    /// The booking's price is a snapshot of the hours it was made with (PRD BR-05). This hour is
    /// being sold now, so it costs what the venue charges now.
    /// </summary>
    [Fact]
    public async Task The_added_hour_costs_what_that_hour_costs_today()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);

        await VenueScenario.SetPricesAsync(owner, venue.Id, VenueScenario.AllWeek(6, 22, 500m));
        var extended = await Extend(owner, venue.Id, booking.Id);

        Assert.Equal(200m, Assert.Single(extended.Slots, slot => slot.Hour == 18).BahtPerHour);
        Assert.Equal(500m, Assert.Single(extended.Slots, slot => slot.Hour == 19).BahtPerHour);
        Assert.Equal(700m, extended.TotalBaht);
    }

    /// <summary>
    /// The money for it is owed on the booking that was already made (PRD US-29), which is the
    /// desk's door to collect through (US-26) — including from a venue that had all of it.
    /// </summary>
    [Fact]
    public async Task What_the_added_hour_costs_is_owed_at_the_desk()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);

        var before = await Row(owner, venue.Id, booking.Id);
        Assert.Equal(0m, before.ToPayBaht);
        Assert.False(before.Can.TakeMoney);

        var extended = await Extend(owner, venue.Id, booking.Id);

        Assert.Equal(200m, extended.ToPayBaht);
        Assert.True(extended.Can.TakeMoney);
        Assert.Equal(nameof(PaymentState.NotReceived), extended.PaymentState);

        // And paying it closes the booking's money again, through the door US-26 already has.
        var paid = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings/{booking.Id}/payments",
                new TakePaymentRequest(200m, nameof(PaymentMethod.Cash), null)));

        Assert.Equal(0m, paid.ToPayBaht);
        Assert.Equal(400m, paid.TakenBaht);
    }

    [Fact]
    public async Task An_hour_somebody_else_holds_is_refused_with_the_courts_that_are_free()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 3);
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);

        // The hour they would run on into, taken on their own court and on one of the others.
        var booker = await scenario.SignedInClientAsync();
        await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 19));
        var second = await scenario.SignedInClientAsync();
        await VenueScenario.HoldAsync(second, venue.Id, Tomorrow, (courts[1], 19));

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/extend", new ExtendBookingRequest(null));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await VenueScenario.ReadAsync<OfferedCourtsProblem>(
            refused, HttpStatusCode.Conflict);
        Assert.Equal(BookingErrorCodes.HourTaken, problem.Code);
        Assert.Equal([courts[2]], problem.Courts.Select(court => court.CourtId));
        Assert.Equal(200m, Assert.Single(problem.Courts).Baht);
    }

    [Fact]
    public async Task The_venue_can_run_them_on_to_a_court_it_names()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);

        var extended = await Extend(owner, venue.Id, booking.Id, courts[1]);

        Assert.Equal(courts[1], Assert.Single(extended.Slots, slot => slot.Hour == 19).CourtId);
    }

    [Fact]
    public async Task An_evening_that_is_over_is_answered_with_a_booking_of_its_own()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        await scenario.PlayOutAsync(booking.Id);

        await Refused(
            owner, venue.Id, booking.Id, "extend", BookingErrorCodes.HoursCannotChange);
    }

    /// <summary>
    /// PRD 6.1 gives a hold two ways out, a slip or the clock. Adding to one would be selling an
    /// hour to somebody who has not paid for the first.
    /// </summary>
    [Fact]
    public async Task A_hold_cannot_be_run_on()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(booker, venue.Id, Tomorrow, (courts[0], 18));

        await Refused(
            owner, venue.Id, booking.Id, "extend", BookingErrorCodes.HoursCannotChange);
    }

    /// <summary>
    /// A suspension stops a venue selling, and an added hour is a sale. Moving is not: the hours
    /// are already theirs, and a court that has flooded still floods (PRD US-20).
    /// </summary>
    [Fact]
    public async Task A_suspended_venue_sells_no_more_hours_but_still_moves_the_ones_it_sold()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);

        var selling = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/extend", new ExtendBookingRequest(null));
        Assert.Equal(HttpStatusCode.Forbidden, selling.StatusCode);

        var moved = await Move(owner, venue.Id, booking.Id, courts[1]);
        Assert.Equal(courts[1], Assert.Single(moved.Slots).CourtId);
    }

    [Fact]
    public async Task The_hours_that_have_not_been_played_go_to_the_other_court_at_the_same_price()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);

        var moved = await Move(owner, venue.Id, booking.Id, courts[1]);

        Assert.Equal(courts[1], Assert.Single(moved.Slots).CourtId);
        Assert.Equal(18, Assert.Single(moved.Slots).Hour);
        Assert.Equal(200m, moved.TotalBaht);
        Assert.Equal(nameof(BookingStatus.Confirmed), moved.Status);

        // The court they left is on sale again, and the one they are on is not.
        var day = await scenario.ReadAvailabilityAsync(owner, venue.Id, Tomorrow);
        Assert.Equal(nameof(HourStatus.Free), StatusOf(day, courts[0], 18));
        Assert.Equal(nameof(HourStatus.Booked), StatusOf(day, courts[1], 18));
    }

    /// <summary>
    /// What was played was played somewhere, and moving that record would be rewriting where
    /// somebody stood. The hour running now still moves: the players are on the court.
    /// </summary>
    [Fact]
    public async Task An_hour_already_finished_stays_on_the_court_it_was_played_on()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);

        // Open all day, priced all day, so this reads the same at whatever hour the suite runs
        // at — the hours below are put in the past, and a venue that was shut then would refuse
        // the move for a reason that has nothing to do with what is being tested.
        await VenueScenario.SetPricesAsync(
            owner, venue.Id, VenueScenario.AllWeek(0, 24, 200m));
        await VenueScenario.SetHoursAsync(owner, venue.Id, VenueScenario.Today, 0, 24);

        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(
            booker, venue.Id, Tomorrow, (courts[0], 18), (courts[0], 19));
        await VenueScenario.UploadAsync(booker, booking.Id, VenueScenario.Jpeg());
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        // The first hour finished half an hour ago; the second is being played.
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-90));

        var moved = await Move(owner, venue.Id, booking.Id, courts[1]);

        Assert.Equal(courts[0], moved.Slots.OrderBy(slot => slot.Hour).First().CourtId);
        Assert.Equal(courts[1], moved.Slots.OrderBy(slot => slot.Hour).Last().CourtId);
    }

    [Fact]
    public async Task A_court_that_is_not_free_for_every_hour_is_refused_with_the_ones_that_are()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 3);
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(
            booker, venue.Id, Tomorrow, (courts[0], 18), (courts[0], 19));
        await VenueScenario.UploadAsync(booker, booking.Id, VenueScenario.Jpeg());
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        // One of the two hours is taken on the court they would move to.
        var other = await scenario.SignedInClientAsync();
        await VenueScenario.HoldAsync(other, venue.Id, Tomorrow, (courts[1], 19));

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/move", new MoveCourtRequest(courts[1]));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await VenueScenario.ReadAsync<OfferedCourtsProblem>(
            refused, HttpStatusCode.Conflict);
        Assert.Equal(BookingErrorCodes.CourtNotFree, problem.Code);
        Assert.Equal([courts[2]], problem.Courts.Select(court => court.CourtId));

        // Moving does not change what anything costs, so the offer carries no amount.
        Assert.Null(Assert.Single(problem.Courts).Baht);
    }

    [Fact]
    public async Task Moving_them_where_they_already_are_is_refused()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/move", new MoveCourtRequest(courts[0]));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(
            BookingErrorCodes.AlreadyOnThatCourt,
            (await VenueScenario.ReadAsync<OfferedCourtsProblem>(
                refused, HttpStatusCode.Conflict)).Code);
    }

    /// <summary>
    /// Every hour that changes is written down with who, when, and what it went from and to
    /// (PRD US-29, PRD 8) — and the rows cannot be rewritten afterwards.
    /// </summary>
    [Fact]
    public async Task Who_changed_which_hour_is_written_down()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);

        await Extend(owner, venue.Id, booking.Id);
        await Move(owner, venue.Id, booking.Id, courts[1]);

        var written = await ChangesAsync(booking.Id);

        var added = Assert.Single(written, change => change.What == HoursChange.Added);
        Assert.Null(added.FromCourtId);
        Assert.Equal(courts[0], added.ToCourtId);
        Assert.Equal(200m, added.BahtPerHour);

        // Both hours moved, each saying where it came from.
        var moved = written.Where(change => change.What == HoursChange.Moved).ToArray();
        Assert.Equal(2, moved.Length);
        Assert.All(moved, change => Assert.Equal(courts[0], change.FromCourtId));
        Assert.All(moved, change => Assert.Equal(courts[1], change.ToCourtId));
        Assert.All(written, change => Assert.NotEqual(Guid.Empty, change.ChangedByUserId));
    }

    /// <summary>
    /// What the counter is offered, so it can offer it without pressing anything (PRD US-29).
    /// </summary>
    [Fact]
    public async Task The_venue_is_told_what_could_still_be_done_with_the_hours()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);

        var possible = await VenueScenario.ReadAsync<BookingHoursResponse>(
            await owner.GetAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/hours"));

        var extend = possible.Extend;
        Assert.NotNull(extend);
        Assert.Equal(19, extend.Hour);
        Assert.Equal(Tomorrow, extend.Date);
        Assert.Equal(courts[0], extend.SameCourtId);
        Assert.Equal(courts.Order(), extend.Courts.Select(court => court.CourtId).Order());

        // Moving is offered only to a court they are not already on.
        var move = possible.Move;
        Assert.NotNull(move);
        Assert.Equal(1, move.Hours);
        Assert.Equal([courts[1]], move.Courts.Select(court => court.CourtId));

        // And nothing is offered about an evening that is over.
        await scenario.PlayOutAsync(booking.Id);
        var over = await VenueScenario.ReadAsync<BookingHoursResponse>(
            await owner.GetAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/hours"));
        Assert.Null(over.Extend);
        Assert.Null(over.Move);
    }

    /// <summary>
    /// Both doors are the venue's, and both are behind the permission the rest of the day's
    /// bookings are behind — the booking names the booker (PDPA, PRD US-13).
    /// </summary>
    [Fact]
    public async Task Staff_without_the_permission_cannot_touch_the_hours()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.VerifySlip));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await staff.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings/{booking.Id}/extend",
                new ExtendBookingRequest(null))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await staff.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings/{booking.Id}/move",
                new MoveCourtRequest(courts[1]))).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await staff.GetAsync(
                $"/api/venues/{venue.Id}/bookings/{booking.Id}/hours")).StatusCode);
    }

    private static string StatusOf(AvailabilityResponse day, Guid courtId, int hour) =>
        day.Courts.Single(court => court.CourtId == courtId).Hours.Single(one => one.Hour == hour).Status;

    private static async Task<VenueBookingResponse> Extend(
        HttpClient owner,
        Guid venueId,
        Guid bookingId,
        Guid? courtId = null) =>
        await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/bookings/{bookingId}/extend",
                new ExtendBookingRequest(courtId)));

    private static async Task<VenueBookingResponse> Move(
        HttpClient owner,
        Guid venueId,
        Guid bookingId,
        Guid courtId) =>
        await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/bookings/{bookingId}/move",
                new MoveCourtRequest(courtId)));

    private static async Task Refused(
        HttpClient owner,
        Guid venueId,
        Guid bookingId,
        string door,
        string code)
    {
        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venueId}/bookings/{bookingId}/{door}", new ExtendBookingRequest(null));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(
            code,
            (await VenueScenario.ReadAsync<OfferedCourtsProblem>(
                refused, HttpStatusCode.Conflict)).Code);
    }

    private async Task<VenueBookingResponse> Row(HttpClient owner, Guid venueId, Guid bookingId)
    {
        var day = await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await owner.GetAsync($"/api/venues/{venueId}/bookings?date={Tomorrow:yyyy-MM-dd}"));
        return day.Single(one => one.BookingId == bookingId);
    }

    private async Task<BookingSlotChange[]> ChangesAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.BookingSlotChanges
            .AsNoTracking()
            .Where(change => change.BookingId == bookingId)
            .OrderBy(change => change.ChangedAt)
            .ThenBy(change => change.StartsAt)
            .ToArrayAsync();
    }

    /// <summary>The refusal as it arrives: a code, and the courts it offers instead.</summary>
    private sealed record OfferedCourtsProblem(string Code, FreeCourtResponse[] Courts);
}
