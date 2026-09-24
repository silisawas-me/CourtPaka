using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>
/// A venue that asks for part of the price before it holds the hours, and what the rest of the
/// system does with the difference (PRD US-28).
/// </summary>
public sealed class DepositTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Theory]
    [InlineData(100, 600)]
    [InlineData(50, 300)]
    [InlineData(10, 60)]
    public void A_share_of_a_price_is_what_it_says(int percent, decimal expected) =>
        Assert.Equal(expected, Deposit.Of(600m, percent));

    /// <summary>
    /// The whole price is answered exactly rather than multiplied out, so that a booking a venue
    /// wants paid in full never ends a satang short of itself.
    /// </summary>
    [Fact]
    public void The_whole_price_is_the_whole_price() =>
        Assert.Equal(333.33m, Deposit.Of(333.33m, Deposit.Everything));

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(101)]
    public void Nothing_and_more_than_everything_are_not_shares(int percent) =>
        Assert.False(Deposit.IsAShare(percent));

    /// <summary>
    /// The venue cannot give back more than it is holding. A booking held on a deposit has had
    /// part of its price arrive, so a policy that gives everything back gives back that part.
    /// </summary>
    [Fact]
    public void A_refund_never_exceeds_what_arrived()
    {
        Assert.Equal(
            300m,
            Refunds.DueFor(
                BookingStatus.Cancelled,
                PaymentState.NotReceived,
                totalBaht: 600m,
                refundPercent: Refunds.AllOfIt,
                heldBaht: 300m));

        // Half the price, of which the venue holds all of it: the terms decide, not the till.
        Assert.Equal(
            300m,
            Refunds.DueFor(
                BookingStatus.Cancelled,
                PaymentState.NotReceived,
                totalBaht: 600m,
                refundPercent: 50,
                heldBaht: 600m));
    }

    /// <summary>
    /// The older answer still stands on its own: a venue that says it has the money has it,
    /// whatever the receipts say — some bookings reached that answer before receipts existed.
    /// </summary>
    [Fact]
    public void A_venue_that_says_it_has_the_money_owes_the_share_of_it() =>
        Assert.Equal(
            600m,
            Refunds.DueFor(
                BookingStatus.Cancelled,
                PaymentState.Received,
                totalBaht: 600m,
                refundPercent: Refunds.AllOfIt,
                heldBaht: 0m));

    [Fact]
    public async Task A_venue_asks_for_a_share_and_the_code_carries_it()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(baht: 300m);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await owner.PutAsJsonAsync($"/api/venues/{venue.Id}/deposit", new DepositRequest(50)))
                .StatusCode);

        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[0], 9), (courts[0], 10));

        Assert.Equal(600m, booking.TotalBaht);
        Assert.Equal(300m, booking.DepositBaht);

        var payment = await VenueScenario.ReadAsync<PaymentResponse>(
            await booker.GetAsync($"/api/bookings/{booking.Id}/payment"));

        Assert.Equal(600m, payment.TotalBaht);
        Assert.Equal(300m, payment.DepositBaht);
        Assert.Equal(300m, payment.PayAtVenueBaht);

        // What a bank app fills in is what the venue is waiting for, not the price.
        Assert.Equal(PromptPay.For("0812345678", 300m), payment.PromptPayPayload);
    }

    /// <summary>
    /// The slip covers the deposit. The booking is confirmed, and the balance is left where the
    /// desk can take it — a venue holding a quarter of the price does not say it has the money
    /// (PRD US-26, 6.2).
    /// </summary>
    [Fact]
    public async Task A_deposit_confirms_the_booking_and_leaves_the_rest_to_the_desk()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(baht: 400m);
        await owner.PutAsJsonAsync($"/api/venues/{venue.Id}/deposit", new DepositRequest(25));

        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 9);
        Assert.Equal(100m, booking.DepositBaht);

        var queue = await VenueScenario.ReadAsync<SlipQueueItemResponse[]>(
            await owner.GetAsync($"/api/venues/{venue.Id}/slip-queue"));
        Assert.Equal(100m, Assert.Single(queue).DepositBaht);

        var confirmed = await VenueScenario.ReadAsync<BookingResponse>(
            await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null));

        Assert.Equal(nameof(BookingStatus.Confirmed), confirmed.Status);

        // Confirmed, and still owed for: the payment state is the venue saying it has all of it.
        Assert.Equal(nameof(PaymentState.NotReceived), confirmed.PaymentState);

        var stored = await scenario.StoredBookingAsync(booking.Id);
        Assert.Equal(PaymentState.NotReceived, stored.PaymentState);

        var day = await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await owner.GetAsync(
                $"/api/venues/{venue.Id}/bookings?date={VenueScenario.Today.AddDays(1):yyyy-MM-dd}"));
        var row = Assert.Single(day, entry => entry.BookingId == booking.Id);

        Assert.Equal(100m, row.TakenBaht);
        Assert.Equal(300m, row.ToPayBaht);
        Assert.True(row.Can.TakeMoney);
    }

    /// <summary>
    /// Letting a booking go gives back what arrived, not what it cost. The booker is shown the
    /// same number the venue would be told to send (PRD US-05, 6.2).
    /// </summary>
    [Fact]
    public async Task Letting_go_gives_back_the_deposit_and_no_more()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(baht: 400m);
        await owner.PutAsJsonAsync($"/api/venues/{venue.Id}/deposit", new DepositRequest(50));

        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 9);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        var cancelled = await VenueScenario.ReadAsync<BookingResponse>(
            await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null));

        Assert.Equal(nameof(BookingStatus.Cancelled), cancelled.Status);
        Assert.Equal(200m, cancelled.RefundDueBaht);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task A_venue_cannot_ask_for_a_share_that_is_not_one(int percent)
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        var refused = await owner.PutAsJsonAsync(
            $"/api/venues/{venue.Id}/deposit", new DepositRequest(percent));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(VenueErrorCodes.InvalidDeposit, await refused.ErrorCodeAsync());
    }

    /// <summary>Every venue asks for the whole price until it says otherwise.</summary>
    [Fact]
    public async Task A_venue_that_says_nothing_asks_for_all_of_it()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync(baht: 250m);
        Assert.Equal(Deposit.Everything, venue.DepositPercent);

        var booker = await scenario.SignedInClientAsync();
        var booking = await VenueScenario.HoldAsync(
            booker, venue.Id, VenueScenario.Today.AddDays(1), (courts[0], 9));

        Assert.Equal(booking.TotalBaht, booking.DepositBaht);
    }
}
