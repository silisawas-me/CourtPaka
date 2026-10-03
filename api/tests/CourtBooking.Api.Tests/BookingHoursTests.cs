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

    /// <summary>
    /// Running an evening on must not change what the venue is recorded as holding. The share it
    /// gives back is of the price, so a booking whose held amount was forgotten would owe nothing
    /// at all — the shape of the bug that refunded four times over (CLAUDE.md, US-28).
    /// </summary>
    [Fact]
    public async Task Adding_an_hour_does_not_change_what_the_venue_is_holding()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(baht: 400m);
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        await Extend(owner, venue.Id, booking.Id);

        // Far enough out that the terms give the whole price back, whatever hour this runs at.
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromDays(2));

        var cancelled = await VenueScenario.ReadAsync<BookingResponse>(
            await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null));

        // What they paid, and not the price of an hour nobody paid for.
        Assert.Equal(400m, cancelled.RefundDueBaht);
    }

    /// <summary>
    /// The slip queue settles money from a price it read before its own transaction, and that
    /// price can now move (PRD US-29). Confirming after an evening has been run on must settle
    /// against what the booking costs now, not against what it cost when the slip arrived —
    /// otherwise the venue records itself as paid in full for an hour nobody paid for, and
    /// `Takings.CanTake` then closes the only door left to collect it.
    /// </summary>
    [Fact]
    public async Task A_slip_checked_after_the_evening_ran_on_does_not_pay_off_the_added_hour()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var extended = await Extend(owner, venue.Id, booking.Id);
        Assert.Equal(400m, extended.TotalBaht);

        // The slip is for the two hundred the booker was asked for, not for the four hundred.
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PostAsync(
                $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null)).StatusCode);

        var row = await Row(owner, venue.Id, booking.Id);
        Assert.Equal(nameof(BookingStatus.Confirmed), row.Status);
        Assert.Equal(200m, row.TakenBaht);

        // The added hour is still owed, and there is still a door to collect it through.
        Assert.Equal(200m, row.ToPayBaht);
        Assert.True(row.Can.TakeMoney);
        Assert.Equal(nameof(PaymentState.NotReceived), row.PaymentState);
    }

    /// <summary>
    /// A booking still waiting to be checked keeps that status after its hours are played — only
    /// a confirmed one reads as completed (PRD 9.2). An evening everybody has gone home from has
    /// nothing to run on into either way.
    /// </summary>
    [Fact]
    public async Task An_unchecked_booking_whose_evening_is_over_cannot_be_run_on()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await scenario.PlayOutAsync(booking.Id);

        await Refused(owner, venue.Id, booking.Id, "extend", BookingErrorCodes.HoursCannotChange);
    }

    /// <summary>
    /// Half a group cannot be run on into an hour the other half is not playing, and one court
    /// cannot take two courts' worth of the same hour. Neither door is offered (PRD US-29).
    /// </summary>
    [Fact]
    public async Task A_booking_on_two_courts_at_once_is_offered_neither_door()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 3);
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(
            booker, venue.Id, Tomorrow, (courts[0], 18), (courts[1], 18));
        await VenueScenario.UploadAsync(booker, booking.Id, VenueScenario.Jpeg());
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        var possible = await VenueScenario.ReadAsync<BookingHoursResponse>(
            await owner.GetAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/hours"));

        Assert.Null(possible.Extend);
        Assert.Null(possible.Move);

        // And the row says the same, so the page draws no button to press.
        var day = await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await owner.GetAsync($"/api/venues/{venue.Id}/bookings?date={Tomorrow:yyyy-MM-dd}"));
        var row = day.Single(one => one.BookingId == booking.Id);
        Assert.False(row.Can.Extend);
        Assert.False(row.Can.MoveCourt);
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

        // Yesterday too: just after midnight the first hour below is yesterday's, and the doors
        // that open courts and set hours will not start them in the past, so both are written
        // straight in — the courts as standing yesterday, the week as open round the clock.
        using (var scope = api.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ownerId = await database.VenueMemberships
                .Where(member => member.VenueId == venue.Id)
                .Select(member => member.UserId)
                .SingleAsync();
            database.OpeningHoursSchedules.Add(OpeningHoursSchedule.Create(
                venue.Id,
                VenueScenario.Today.AddDays(-1),
                Enum.GetValues<DayOfWeek>().Select(day => new WeekdayHours(day, 0, 24)),
                ownerId,
                DateTimeOffset.UtcNow.AddMinutes(-1)));
            database.CourtStatusChanges.AddRange(courts.Select(court => new CourtStatusChange
            {
                CourtId = court,
                Active = true,
                EffectiveFrom = VenueScenario.Today.AddDays(-1),
                ChangedByUserId = ownerId,
                ChangedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            }));
            await database.SaveChangesAsync();
        }

        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(
            booker, venue.Id, Tomorrow, (courts[0], 18), (courts[0], 19));
        await VenueScenario.UploadAsync(booker, booking.Id, VenueScenario.Jpeg());
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        // The first hour finished half an hour ago; the second is being played.
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-90));

        var moved = await Move(owner, venue.Id, booking.Id, courts[1]);

        // In the order they are played, which is not the order of their hour numbers: the two
        // hours straddle midnight whenever the suite runs just after it, and 23 then sorts after 0.
        Assert.Equal([courts[0], courts[1]], moved.Slots.Select(slot => slot.CourtId));
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

    /// <summary>
    /// A booking can hold two courts in the same hour, and one court cannot hold both. The answer
    /// has to be one the screen can read, not a 500 (PRD US-23).
    /// </summary>
    [Fact]
    public async Task Two_courts_at_once_cannot_be_moved_onto_one()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 3);
        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(
            booker, venue.Id, Tomorrow, (courts[0], 18), (courts[1], 18));
        await VenueScenario.UploadAsync(booker, booking.Id, VenueScenario.Jpeg());
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/move", new MoveCourtRequest(courts[2]));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(
            BookingErrorCodes.HoursOverlap,
            (await VenueScenario.ReadAsync<OfferedCourtsProblem>(
                refused, HttpStatusCode.Conflict)).Code);

        // And nothing moved: the booking is where it was.
        var day = await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await owner.GetAsync($"/api/venues/{venue.Id}/bookings?date={Tomorrow:yyyy-MM-dd}"));
        var row = day.Single(one => one.BookingId == booking.Id);
        Assert.Equal(
            [courts[0], courts[1]],
            row.Slots.Select(slot => slot.CourtId).Order());
    }

    /// <summary>A booking of another venue is not this venue's to change (PDPA, PRD US-13).</summary>
    [Fact]
    public async Task A_booking_of_another_venue_is_not_found_here()
    {
        var (mine, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var (theirs, other, otherCourts) = await scenario.BookableVenueAsync();
        var (_, elsewhere) = await scenario.ConfirmedBookingAsync(
            theirs, other.Id, otherCourts[0], 18);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await mine.GetAsync(
                $"/api/venues/{venue.Id}/bookings/{elsewhere.Id}/hours")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await mine.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings/{elsewhere.Id}/move",
                new MoveCourtRequest(courts[1]))).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await mine.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings/{elsewhere.Id}/extend",
                new ExtendBookingRequest(null))).StatusCode);
    }

    /// <summary>A court of another venue is not somewhere this booking can be sent.</summary>
    [Fact]
    public async Task A_court_of_another_venue_is_no_court_at_all()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, __, elsewhere) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);

        foreach (var door in new[] { "extend", "move" })
        {
            var refused = await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings/{booking.Id}/{door}",
                new MoveCourtRequest(elsewhere[0]));

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(
                BookingErrorCodes.CourtUnknown,
                (await VenueScenario.ReadAsync<OfferedCourtsProblem>(
                    refused, HttpStatusCode.BadRequest)).Code);
        }
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

    /// <summary>
    /// The owner app's "−1 ชม.": the last hour comes off before it begins, the price comes down
    /// by what that hour was sold for, and the hour is free to sell again.
    /// </summary>
    [Fact]
    public async Task The_last_hour_comes_off_before_it_begins()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        await Extend(owner, venue.Id, booking.Id);

        var offered = await VenueScenario.ReadAsync<BookingHoursResponse>(
            await owner.GetAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/hours"));
        Assert.Equal(19, offered.Shorten!.Hour);
        Assert.Equal(200m, offered.Shorten.Baht);

        var shorter = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/shorten", null));

        Assert.Equal([18], shorter.Slots.Select(slot => slot.Hour));
        Assert.Equal(200m, shorter.TotalBaht);
        // What was paid is the whole of the new price again.
        Assert.Equal(nameof(PaymentState.Received), shorter.PaymentState);
        Assert.False(shorter.Can.TakeMoney);

        var removed = Assert.Single(await ChangesAsync(booking.Id), change => change.What == HoursChange.Removed);
        Assert.Equal(courts[0], removed.FromCourtId);

        // The hour is somebody else's to buy now: running the evening on takes it again.
        Assert.Equal(400m, (await Extend(owner, venue.Id, booking.Id)).TotalBaht);
    }

    [Fact]
    public async Task A_booking_of_one_hour_has_nothing_to_give_back()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);

        var offered = await VenueScenario.ReadAsync<BookingHoursResponse>(
            await owner.GetAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/hours"));
        Assert.Null(offered.Shorten);

        var refused = await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/shorten", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.HoursCannotChange, await refused.ErrorCodeAsync());
    }

    /// <summary>Money in for an hour that would no longer be theirs is a refund, not this door's.</summary>
    [Fact]
    public async Task An_hour_already_paid_for_does_not_come_off()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        await Extend(owner, venue.Id, booking.Id);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/payments",
            new TakePaymentRequest(200m, nameof(PaymentMethod.Cash), null));

        // Not even offered: the counter is not shown a door that would refuse it.
        var offered = await VenueScenario.ReadAsync<BookingHoursResponse>(
            await owner.GetAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/hours"));
        Assert.Null(offered.Shorten);

        var refused = await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/shorten", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.ShortenAlreadyPaid, await refused.ErrorCodeAsync());
        Assert.Equal(400m, (await Row(owner, venue.Id, booking.Id)).TotalBaht);
    }

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
