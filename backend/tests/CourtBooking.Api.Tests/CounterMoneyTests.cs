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
        Assert.Equal(booking.TotalBaht - 100m, deposit.OutstandingBaht);

        var rest = await Take(
            owner, venue.Id, booking.Id, deposit.OutstandingBaht, nameof(PaymentMethod.Card));

        Assert.Equal(booking.TotalBaht, rest.TakenBaht);
        Assert.Equal(0m, rest.OutstandingBaht);
        Assert.Equal(2, rest.Receipts.Length);
        // Each one says what form it came in, because that is what the till is counted against.
        Assert.Equal(
            [nameof(PaymentMethod.Cash), nameof(PaymentMethod.Card)],
            rest.Receipts.Select(receipt => receipt.Method));
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

    private static async Task<TakingsResponse> Take(
        HttpClient client, Guid venueId, Guid bookingId, decimal amount, string method) =>
        await VenueScenario.ReadAsync<TakingsResponse>(
            await client.PostAsJsonAsync(
                $"/api/venues/{venueId}/bookings/{bookingId}/payments",
                new TakePaymentRequest(amount, method, null)));

    private static async Task<TakingsResponse> Takings(
        HttpClient client, Guid venueId, Guid bookingId) =>
        await VenueScenario.ReadAsync<TakingsResponse>(
            await client.GetAsync($"/api/venues/{venueId}/bookings/{bookingId}/payments"));

    private static async Task<DayMoneyResponse> Money(HttpClient client, Guid venueId) =>
        await VenueScenario.ReadAsync<DayMoneyResponse>(
            await client.GetAsync($"/api/venues/{venueId}/money"));

    private static async Task<VenueBookingResponse> Row(
        HttpClient client, Guid venueId, Guid bookingId)
    {
        var day = await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await client.GetAsync(
                $"/api/venues/{venueId}/bookings?date={VenueScenario.Today.AddDays(1):yyyy-MM-dd}"));
        return day.Single(booking => booking.BookingId == bookingId);
    }
}
