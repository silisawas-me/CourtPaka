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
    /// part of its price arrive, so terms that give everything back give back that part.
    /// </summary>
    [Fact]
    public void A_refund_never_exceeds_what_arrived() =>
        Assert.Equal(
            300m,
            Refunds.DueFor(
                BookingStatus.Cancelled,
                PaymentState.NotReceived,
                totalBaht: 600m,
                refundPercent: Refunds.AllOfIt,
                takenBaht: 300m,
                askedBaht: 300m));

    /// <summary>
    /// The share is of the price, not of the payment. Terms that give half of a 600 booking back
    /// leave the venue entitled to 300 of it — so a booker who paid 300 as a deposit is owed
    /// nothing, and one who paid all of it is owed 300. Taking half of what happened to arrive
    /// would hand the venue's whole cancellation fee to anybody paying a deposit.
    /// </summary>
    [Theory]
    [InlineData(300, 0)]
    [InlineData(450, 150)]
    [InlineData(600, 300)]
    public void What_the_terms_let_the_venue_keep_comes_out_of_what_it_holds(
        decimal taken,
        decimal owed) =>
        Assert.Equal(
            owed,
            Refunds.DueFor(
                BookingStatus.Cancelled,
                PaymentState.NotReceived,
                totalBaht: 600m,
                refundPercent: 50,
                takenBaht: taken,
                askedBaht: 300m));

    /// <summary>
    /// The older answer still stands on its own, but only for what was asked for: a venue saying
    /// the money arrived about a booking held on a 300 deposit is saying 300 arrived, not 600.
    /// Every booking made before deposits existed was asked for its whole price, so those are
    /// unchanged (the migration says so).
    /// </summary>
    [Fact]
    public void A_venue_that_says_it_has_the_money_has_what_it_asked_for()
    {
        Assert.Equal(
            600m,
            Refunds.DueFor(
                BookingStatus.Cancelled,
                PaymentState.Received,
                totalBaht: 600m,
                refundPercent: Refunds.AllOfIt,
                takenBaht: 0m,
                askedBaht: 600m));

        Assert.Equal(
            300m,
            Refunds.DueFor(
                BookingStatus.Cancelled,
                PaymentState.Received,
                totalBaht: 600m,
                refundPercent: Refunds.AllOfIt,
                takenBaht: 0m,
                askedBaht: 300m));
    }

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

        // The terms give everything back with a day's notice, and a booking made for nine
        // o'clock tomorrow has that only until nine o'clock this morning — so what this asserts
        // depended on the hour the suite happened to run at. The hours are put far enough out
        // for the notice to be the same every time.
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromDays(2));

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

    /// <summary>
    /// The venue answering "the money arrived" weeks later is answering about the deposit, which
    /// is what it asked for. Before this was said out loud, a 25% booking cancelled at that point
    /// left the venue owing four times what it had been sent (PRD 6.2, US-13).
    /// </summary>
    [Fact]
    public async Task Settling_a_deposit_booking_owes_back_the_deposit()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(baht: 400m);
        await owner.PutAsJsonAsync($"/api/venues/{venue.Id}/deposit", new DepositRequest(25));

        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 9);

        // Cancelled while the slip was still waiting: nobody has said whether the money arrived.
        var cancelled = await VenueScenario.ReadAsync<BookingResponse>(
            await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null));
        Assert.Equal(nameof(PaymentState.Unconfirmed), cancelled.PaymentState);
        Assert.Equal(0m, cancelled.RefundDueBaht);

        var settled = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings/{booking.Id}/settle-payment",
                new SettlePaymentRequest(true)));

        Assert.Equal(100m, settled.RefundDueBaht);
    }

    /// <summary>
    /// A booker can type over the amount in a bank app. The venue is looking at the slip, so it
    /// is the one who can say what actually arrived — and if it could not, the desk would ask for
    /// money that is already in the account (PRD US-28, BR-07).
    /// </summary>
    [Fact]
    public async Task The_venue_records_what_the_slip_says_arrived()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(baht: 400m);
        await owner.PutAsJsonAsync($"/api/venues/{venue.Id}/deposit", new DepositRequest(25));

        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 9);

        var confirmed = await VenueScenario.ReadAsync<BookingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm",
                new ConfirmSlipRequest(400m)));

        // All of it arrived, so the venue has the money and the desk is asked for nothing.
        Assert.Equal(nameof(PaymentState.Received), confirmed.PaymentState);
        Assert.Equal(0m, confirmed.ToPayBaht);
    }

    [Fact]
    public async Task A_slip_cannot_be_recorded_as_more_than_the_booking_costs()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(baht: 400m);
        await owner.PutAsJsonAsync($"/api/venues/{venue.Id}/deposit", new DepositRequest(25));

        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 9);

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm",
            new ConfirmSlipRequest(401m));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(SlipErrorCodes.InvalidAmount, await refused.ErrorCodeAsync());
    }

    /// <summary>
    /// What the venue is holding is what it earned, so a booking played on a deposit alone is
    /// revenue for the deposit — not nothing, and not its price (PRD US-15).
    /// </summary>
    [Fact]
    public async Task A_deposit_that_was_played_is_revenue_for_what_arrived()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync(baht: 400m);
        await owner.PutAsJsonAsync($"/api/venues/{venue.Id}/deposit", new DepositRequest(25));

        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 9);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        // Moved into yesterday, so the hours are played and the money is revenue rather than
        // still to come.
        var yesterday = VenueScenario.Today.AddDays(-1);
        await PlayOnAsync(booking.Id, yesterday, 9);

        var figures = await VenueScenario.ReadAsync<DashboardResponse>(
            await owner.GetAsync(
                $"/api/venues/{venue.Id}/dashboard?from={yesterday:yyyy-MM-dd}&to={yesterday:yyyy-MM-dd}"));

        Assert.Equal(100m, figures.OnlineBaht);
    }

    /// <summary>Puts a booking's hours on a day that is over, in the database.</summary>
    private async Task PlayOnAsync(Guid bookingId, DateOnly date, int hour)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var starts = PlatformRequirements.BangkokHour(date, hour);

        await database.BookingSlots
            .Where(slot => slot.BookingId == bookingId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(slot => slot.StartsAt, starts)
                .SetProperty(slot => slot.EndsAt, starts.AddHours(1)));
    }
}
