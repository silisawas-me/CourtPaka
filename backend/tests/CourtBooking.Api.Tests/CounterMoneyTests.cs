using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>
/// Money as it crosses the counter, and the count at the end of the day (PRD US-26): taken in
/// parts, in the form it arrived, and never more than is owed.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CounterMoneyTests(ApiTestFixture api)
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task A_booking_is_paid_for_in_parts_and_the_rest_is_what_is_left()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var deposit = await Take(owner, venue.Id, booking.Id, 100m, nameof(PaymentMethod.Cash));
        Assert.Equal(100m, deposit.TakenBaht);
        Assert.Equal(booking.TotalBaht - 100m, deposit.ToPayBaht);

        var rest = await Take(
            owner, venue.Id, booking.Id, deposit.ToPayBaht, nameof(PaymentMethod.Card));

        Assert.Equal(booking.TotalBaht, rest.TakenBaht);
        Assert.Equal(0m, rest.ToPayBaht);

        var takings = await Takings(owner, venue.Id, booking.Id);
        Assert.Equal(2, takings.Receipts.Length);
        // Each one says what form it came in, because that is what the till is counted against.
        Assert.Equal(
            [nameof(PaymentMethod.Cash), nameof(PaymentMethod.Card)],
            takings.Receipts.Select(receipt => receipt.Method));
    }

    /// <summary>A till that says it took more than was owed is a till nobody can count.</summary>
    [Fact]
    public async Task More_than_is_owed_is_refused()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var tooMuch = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/payments",
            new TakePaymentRequest(booking.TotalBaht + 1m, nameof(PaymentMethod.Cash), null));

        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal(MoneyErrorCodes.InvalidAmount, await tooMuch.ErrorCodeAsync());

        var nothing = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/payments",
            new TakePaymentRequest(0m, nameof(PaymentMethod.Cash), null));
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);

        var madeUp = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/payments",
            new TakePaymentRequest(50m, "Bitcoin", null));
        Assert.Equal(HttpStatusCode.BadRequest, madeUp.StatusCode);
        Assert.Equal(MoneyErrorCodes.UnknownMethod, await madeUp.ErrorCodeAsync());
    }

    /// <summary>
    /// Paid in full is what the rest of the system reads as "the venue has the money" (PRD 6.2),
    /// so taking the last of it is what says so — not a second button.
    /// </summary>
    [Fact]
    public async Task Paying_the_last_of_it_is_what_makes_the_booking_paid_for()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 19);

        await Take(owner, venue.Id, booking.Id, 50m, nameof(PaymentMethod.Cash));
        Assert.Equal("NotReceived", (await Row(owner, venue.Id, booking.Id)).PaymentState);

        await Take(owner, venue.Id, booking.Id, booking.TotalBaht - 50m, nameof(PaymentMethod.Cash));

        var row = await Row(owner, venue.Id, booking.Id);
        Assert.Equal("Received", row.PaymentState);
        Assert.Equal(booking.TotalBaht, row.TakenBaht);
        Assert.Equal(0m, row.ToPayBaht);
    }

    /// <summary>
    /// Nobody types in what a booker transferred, so a slip the venue accepted writes its own
    /// receipt — a day whose transfers are missing is a day that does not add up (PRD US-26).
    /// </summary>
    [Fact]
    public async Task A_slip_the_venue_accepted_is_money_the_day_knows_about()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 20);

        var money = await Money(owner, venue.Id);

        Assert.Equal(booking.TotalBaht, money.PromptPayBaht);
        Assert.Equal(0m, money.CashBaht);
        Assert.Equal(booking.TotalBaht, (await Takings(owner, venue.Id, booking.Id)).TakenBaht);
    }

    /// <summary>
    /// A deposit at the desk and the rest transferred is two receipts that add up to the price.
    /// The slip covers what the desk has not taken, so the booking does not end up paid for with
    /// a door still open on it, and the day's transfers are not short (PRD US-26).
    /// </summary>
    [Fact]
    public async Task A_deposit_at_the_desk_and_the_rest_transferred_add_up()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        await Take(owner, venue.Id, booking.Id, 50m, nameof(PaymentMethod.Cash));
        var confirmed = await owner.PostAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);
        Assert.True(confirmed.IsSuccessStatusCode);

        var takings = await Takings(owner, venue.Id, booking.Id);
        Assert.Equal(booking.TotalBaht, takings.TakenBaht);
        Assert.Equal(0m, takings.OutstandingBaht);

        var money = await Money(owner, venue.Id);
        Assert.Equal(50m, money.CashBaht);
        Assert.Equal(booking.TotalBaht - 50m, money.PromptPayBaht);

        // And nothing is asked for twice.
        Assert.False((await Row(owner, venue.Id, booking.Id)).Can.TakeMoney);
    }

    /// <summary>What was sold at the counter was already paid for, in the form it was paid.</summary>
    [Fact]
    public async Task What_the_counter_sold_is_counted_in_the_form_it_was_paid()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var sold = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings",
                new CounterBookingRequest(
                    [new BookingSlotRequest(courts[0], VenueScenario.Today.AddDays(1), 21)],
                    "คุณต้น",
                    null,
                    nameof(CounterPayment.Cash))),
            HttpStatusCode.Created);

        var money = await Money(owner, venue.Id);

        Assert.Equal(sold.TotalBaht, money.CashBaht);
        Assert.Equal(0m, sold.ToPayBaht);
    }

    /// <summary>
    /// A booking waiting on a slip that is paid another way is not waiting for anything: the
    /// money arriving is the same answer the slip queue gives, so it is confirmed here rather
    /// than left waiting for a slip that is never coming (PRD US-12, US-26).
    /// </summary>
    [Fact]
    public async Task Paying_the_last_of_it_at_the_desk_confirms_a_booking_waiting_on_a_slip()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        await Take(owner, venue.Id, booking.Id, booking.TotalBaht, nameof(PaymentMethod.Cash));

        var row = await Row(owner, venue.Id, booking.Id);
        Assert.Equal(nameof(BookingStatus.Confirmed), row.Status);
        Assert.Equal("Received", row.PaymentState);

        // And the move is in the history like every other one, with the person who took the
        // money against it (PRD 6.1).
        var history = await scenario.HistoryAsync(booking.Id);
        Assert.Equal(BookingStatus.PendingVerification, history[^1].From);
        Assert.Equal(BookingStatus.Confirmed, history[^1].To);
        Assert.NotNull(history[^1].ChangedByUserId);
    }

    /// <summary>
    /// A hold has two ways out and neither of them is the till (PRD 6.1): a booker who paid at
    /// the desk is sold the hours at the counter, which is the answer the state machine has. A
    /// hold that has run out is further out still — it owes nothing forwards, and money against
    /// it would be a refund question wearing the wrong hat (PRD US-26).
    /// </summary>
    [Fact]
    public async Task Money_is_not_taken_against_a_hold()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var held = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[0], 19));

        var waiting = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{held.Id}/payments",
            new TakePaymentRequest(held.TotalBaht, nameof(PaymentMethod.Cash), null));

        Assert.Equal(HttpStatusCode.Conflict, waiting.StatusCode);
        Assert.Equal(MoneyErrorCodes.NotTakeable, await waiting.ErrorCodeAsync());

        await scenario.LapseHoldAsync(held.Id);
        var lapsed = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{held.Id}/payments",
            new TakePaymentRequest(held.TotalBaht, nameof(PaymentMethod.Cash), null));

        Assert.Equal(HttpStatusCode.Conflict, lapsed.StatusCode);
        Assert.Equal(MoneyErrorCodes.NotTakeable, await lapsed.ErrorCodeAsync());

        // And the day sees nothing, because nothing came in.
        Assert.Equal(0m, (await Money(owner, venue.Id)).TakenBaht);
    }

    /// <summary>
    /// The door the counter is shown is the door the server opens: the row says whether there is
    /// money to take on it, and it is the same rule both times (PRD US-13, US-26).
    /// </summary>
    [Fact]
    public async Task The_row_says_whether_there_is_money_to_take_on_it()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 20);

        Assert.True((await Row(owner, venue.Id, booking.Id)).Can.TakeMoney);

        await Take(owner, venue.Id, booking.Id, booking.TotalBaht, nameof(PaymentMethod.Cash));

        Assert.False((await Row(owner, venue.Id, booking.Id)).Can.TakeMoney);
    }

    /// <summary>
    /// Money taken after the till has been counted belongs to the next day (PRD US-26). The count
    /// that was written down cannot move, so the day after it picks the money up — which is why a
    /// venue's day runs from one count to the next rather than from midnight to midnight.
    /// </summary>
    [Fact]
    public async Task Money_taken_after_the_count_belongs_to_the_next_day()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, first) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await Take(owner, venue.Id, first.Id, 100m, nameof(PaymentMethod.Cash));

        var counted = await VenueScenario.ReadAsync<DailyClosingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/money/closing",
                new CloseDayRequest(0m, 100m, null)));
        Assert.Equal(0m, counted.DifferenceBaht);

        // Somebody pays after the drawer has been counted.
        var (_, late) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 19);
        await Take(owner, venue.Id, late.Id, 150m, nameof(PaymentMethod.Cash));

        // Today still says what it was counted against — the count is written down and does not
        // move — and the late money is not in it.
        var today = await Money(owner, venue.Id);
        Assert.Equal(100m, today.CashBaht);
        Assert.DoesNotContain(today.CashReceipts, receipt => receipt.AmountBaht == 150m);

        // Tomorrow has it.
        var tomorrow = await Money(owner, venue.Id, VenueScenario.Today.AddDays(1));
        Assert.Equal(150m, tomorrow.CashBaht);
        Assert.Contains(tomorrow.CashReceipts, receipt => receipt.AmountBaht == 150m);

        // Which is where it will be counted: a day that has not happened yet cannot be closed,
        // so the money waits for the next count rather than being lost between two.
        Assert.Null(tomorrow.Closed);
    }

    /// <summary>
    /// A count that comes days late closes its own day and no more. A venue catching up on a week
    /// it never counted must not have one of those days swallow all the others (PRD US-26).
    /// </summary>
    [Fact]
    public async Task A_count_that_comes_late_still_only_counts_its_own_day()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await Take(owner, venue.Id, booking.Id, 200m, nameof(PaymentMethod.Cash));

        // Yesterday was never counted, and is counted now — a day after the money came in.
        var late = VenueScenario.Today.AddDays(-1);
        var counted = await VenueScenario.ReadAsync<DailyClosingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/money/closing?date={late:yyyy-MM-dd}",
                new CloseDayRequest(0m, 0m, null)));

        // Nothing was taken yesterday, so nothing is what it should hold — today's money is
        // today's, whatever hour the count happened to be written at.
        Assert.Equal(0m, counted.ExpectedCashBaht);
        Assert.Equal(0m, counted.DifferenceBaht);

        var today = await Money(owner, venue.Id);
        Assert.Equal(200m, today.CashBaht);
    }

    /// <summary>
    /// The count: what the till should hold is the float plus the cash that came in, less the
    /// cash that went back out — and the difference is the server's arithmetic (PRD US-26).
    /// </summary>
    [Fact]
    public async Task The_day_is_counted_once_and_says_what_is_missing()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        // The whole booking, in cash, so the till and the day's takings are the same number.
        await Take(owner, venue.Id, booking.Id, booking.TotalBaht, nameof(PaymentMethod.Cash));
        var expected = 1_000m + booking.TotalBaht;

        // A hundred short of what the float and the day's cash say should be there.
        var closing = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/money/closing",
            new CloseDayRequest(1_000m, expected - 100m, "ขาดร้อยนึง"));
        Assert.True(
            closing.IsSuccessStatusCode,
            $"closing the day answered {(int)closing.StatusCode} {await closing.ErrorCodeAsync()}");
        var counted = await VenueScenario.ReadAsync<DailyClosingResponse>(closing);

        Assert.Equal(expected, counted.ExpectedCashBaht);
        Assert.Equal(-100m, counted.DifferenceBaht);
        Assert.Equal("ขาดร้อยนึง", counted.Note);

        // A day is counted once. The second person to press is told so.
        var again = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/money/closing",
            new CloseDayRequest(1_000m, expected, null));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(MoneyErrorCodes.AlreadyClosed, await again.ErrorCodeAsync());

        // And the day says it has been counted, with the cash it was counted against.
        var money = await Money(owner, venue.Id);
        Assert.NotNull(money.Closed);
        Assert.Equal(-100m, money.Closed!.DifferenceBaht);
        Assert.Contains(money.CashReceipts, receipt => receipt.AmountBaht == booking.TotalBaht);
    }

    /// <summary>
    /// A till that did not balance is told where to look: the rows whose amount is exactly what
    /// it came out by, and nothing looser than exactly (PRD US-26). A day that balanced is told
    /// nothing, because there is nothing to explain.
    /// </summary>
    [Fact]
    public async Task A_count_that_did_not_balance_says_which_rows_are_that_amount()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        // Half of it in cash, so the deposit and what is still owed are the same amount.
        var half = booking.TotalBaht / 2;
        await Take(owner, venue.Id, booking.Id, half, nameof(PaymentMethod.Cash));

        // The till is short by exactly that, which both the deposit and the rest would explain.
        var closed = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/money/closing",
            new CloseDayRequest(1_000m, 1_000m, null));
        Assert.True(closed.IsSuccessStatusCode);

        var money = await Money(owner, venue.Id);
        Assert.Equal(-half, money.Closed!.DifferenceBaht);

        var lead = Assert.Single(
            money.Leads, one => one.Kind == nameof(MoneyLeadKind.CashTaken));
        Assert.Equal(half, lead.AmountBaht);
        Assert.Equal(booking.Id, lead.BookingId);

        // What the booking still owes is not offered here, and should not be: it is played
        // tomorrow, so it is tomorrow's outstanding, not this day's (PRD US-15's rule, by
        // service date).
        Assert.DoesNotContain(money.Leads, one => one.Kind == nameof(MoneyLeadKind.StillOwed));
    }

    /// <summary>A day that came out even has nothing to explain, so nothing is offered.</summary>
    [Fact]
    public async Task A_count_that_balanced_is_offered_nothing()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 19);
        await Take(owner, venue.Id, booking.Id, booking.TotalBaht, nameof(PaymentMethod.Cash));

        var before = await Money(owner, venue.Id);
        var closed = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/money/closing",
            new CloseDayRequest(1_000m, 1_000m + before.CashBaht - before.CashRefundedBaht, null));
        Assert.True(closed.IsSuccessStatusCode);

        var money = await Money(owner, venue.Id);
        Assert.Equal(0m, money.Closed!.DifferenceBaht);
        Assert.Empty(money.Leads);
    }

    /// <summary>A day nobody has counted is not guessing at anything yet.</summary>
    [Fact]
    public async Task A_day_that_has_not_been_counted_offers_nothing()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 20);
        await Take(owner, venue.Id, booking.Id, 100m, nameof(PaymentMethod.Cash));

        var money = await Money(owner, venue.Id);

        Assert.Null(money.Closed);
        Assert.Empty(money.Leads);
    }

    [Fact]
    public async Task A_day_that_has_not_happened_cannot_be_counted()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        var tomorrow = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/money/closing?date={VenueScenario.Today.AddDays(1):yyyy-MM-dd}",
            new CloseDayRequest(1_000m, 1_000m, null));

        Assert.Equal(HttpStatusCode.Conflict, tomorrow.StatusCode);
        Assert.Equal(MoneyErrorCodes.DayNotOver, await tomorrow.ErrorCodeAsync());
    }

    /// <summary>
    /// Counting the till is for whoever is trusted with the numbers (ViewReports), and taking
    /// money is for whoever works the counter (ManageBookings). They are not the same person.
    /// </summary>
    [Fact]
    public async Task Each_door_asks_for_what_it_needs()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        var counterOnly = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.ManageBookings));

        Assert.Equal(
            HttpStatusCode.OK,
            (await counterOnly.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings/{booking.Id}/payments",
                new TakePaymentRequest(10m, nameof(PaymentMethod.Cash), null))).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await counterOnly.GetAsync($"/api/venues/{venue.Id}/money")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await counterOnly.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/money/closing",
                new CloseDayRequest(0m, 0m, null))).StatusCode);
    }

    /// <summary>
    /// A day that has been counted stays counted (PRD US-26). Money still arrives after the till
    /// is shut — somebody pays for the court they are standing on — and it goes into tomorrow's
    /// drawer, because it is not in the one that was just counted and signed off.
    /// </summary>
    [Fact]
    public async Task Money_taken_after_the_till_is_counted_is_the_next_day_s()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        await Take(owner, venue.Id, booking.Id, 100m, nameof(PaymentMethod.Cash));

        var closed = await VenueScenario.ReadAsync<DailyClosingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/money/closing",
                new CloseDayRequest(1_000m, 1_100m, null)));
        Assert.Equal(0m, closed.DifferenceBaht);

        // The rest is paid after the count, at the desk, in cash.
        await Take(owner, venue.Id, booking.Id, 100m, nameof(PaymentMethod.Cash));

        // Today is what it was counted as: the count that was signed off does not move.
        var today = await Money(owner, venue.Id);
        Assert.Equal(100m, today.CashBaht);
        Assert.Single(today.CashReceipts);
        Assert.Equal(0m, today.Closed!.DifferenceBaht);

        // And the money is in tomorrow's drawer, where somebody will count it.
        var tomorrow = await MoneyOn(owner, venue.Id, VenueScenario.Today.AddDays(1));
        Assert.Equal(100m, tomorrow.CashBaht);
        Assert.Equal(100m, Assert.Single(tomorrow.CashReceipts).AmountBaht);
        Assert.Null(tomorrow.Closed);
    }

    /// <summary>
    /// The booking still knows it has been paid in full. Which day the money is counted in is a
    /// question about the till, not about what the booker owes (PRD US-26).
    /// </summary>
    [Fact]
    public async Task What_a_booking_has_been_paid_does_not_depend_on_the_till_being_counted()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/money/closing", new CloseDayRequest(0m, 0m, null));

        var paid = await Take(owner, venue.Id, booking.Id, booking.TotalBaht, nameof(PaymentMethod.Cash));

        Assert.Equal(0m, paid.ToPayBaht);
        Assert.Equal(booking.TotalBaht, paid.TakenBaht);
        Assert.Equal(nameof(PaymentState.Received), paid.PaymentState);
    }

    private static async Task<VenueBookingResponse> Take(
        HttpClient client, Guid venueId, Guid bookingId, decimal amount, string method) =>
        await VenueScenario.ReadAsync<VenueBookingResponse>(
            await client.PostAsJsonAsync(
                $"/api/venues/{venueId}/bookings/{bookingId}/payments",
                new TakePaymentRequest(amount, method, null)));

    private static async Task<TakingsResponse> Takings(
        HttpClient client, Guid venueId, Guid bookingId) =>
        await VenueScenario.ReadAsync<TakingsResponse>(
            await client.GetAsync($"/api/venues/{venueId}/bookings/{bookingId}/payments"));

    private static async Task<DayMoneyResponse> Money(
        HttpClient client,
        Guid venueId,
        DateOnly? date = null) =>
        await VenueScenario.ReadAsync<DayMoneyResponse>(
            await client.GetAsync(
                date is { } day
                    ? $"/api/venues/{venueId}/money?date={day:yyyy-MM-dd}"
                    : $"/api/venues/{venueId}/money"));

    private static async Task<DayMoneyResponse> MoneyOn(
        HttpClient client, Guid venueId, DateOnly date) =>
        await VenueScenario.ReadAsync<DayMoneyResponse>(
            await client.GetAsync($"/api/venues/{venueId}/money?date={date:yyyy-MM-dd}"));

    private static async Task<VenueBookingResponse> Row(
        HttpClient client, Guid venueId, Guid bookingId)
    {
        var day = await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await client.GetAsync(
                $"/api/venues/{venueId}/bookings?date={VenueScenario.Today.AddDays(1):yyyy-MM-dd}"));
        return day.Single(booking => booking.BookingId == bookingId);
    }
}
