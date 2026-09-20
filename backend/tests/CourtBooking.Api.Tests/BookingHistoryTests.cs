using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Tests.Infrastructure;

namespace CourtBooking.Api.Tests;

/// <summary>What a booker sees of their own bookings, and letting one go: PRD US-05, 6.1, 6.2.</summary>
public sealed class BookingHistoryTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task The_list_holds_what_is_ahead_and_what_is_behind()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var booker = await scenario.SignedInClientAsync();

        // A hold that ran out is behind them, whatever day its hours fall on — and letting it run
        // out is also what frees the booker to hold something else (PRD S-22).
        var lapsed = await HoldAsync(booker, venue.Id, courts[1], 18);
        await scenario.LapseHoldAsync(lapsed.Id);

        var ahead = await HoldAsync(booker, venue.Id, courts[0], 18);

        var history = await MineAsync(booker);

        Assert.Equal(ahead.Id, Assert.Single(history.Upcoming).Id);
        var behind = Assert.Single(history.Past);
        Assert.Equal(lapsed.Id, behind.Id);
        Assert.Equal(nameof(BookingStatus.Expired), behind.Status);
    }

    [Fact]
    public async Task A_booker_sees_their_own_bookings_and_nobody_else_s()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var mine = await scenario.SignedInClientAsync();
        var theirs = await scenario.SignedInClientAsync();
        await HoldAsync(mine, venue.Id, courts[0], 18);
        await HoldAsync(theirs, venue.Id, courts[1], 18);

        var history = await MineAsync(mine);

        Assert.Single(history.Upcoming);
        Assert.Empty(history.Past);
    }

    [Fact]
    public async Task Letting_a_hold_go_frees_the_hours_and_owes_nothing()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await HoldAsync(booker, venue.Id, courts[0], 18);

        // Nothing has been paid, so the offer says nothing comes back.
        Assert.True(booking.Cancellation.Allowed);
        Assert.Equal(0, booking.Cancellation.RefundPercent);

        var cancelled = await CancelAsync(booker, booking.Id);

        Assert.Equal(nameof(BookingStatus.Cancelled), cancelled.Status);
        Assert.Equal(0m, cancelled.RefundDueBaht);
        Assert.False(cancelled.Cancellation.Allowed);

        // Someone else can take the hour straight away (PRD 6.1).
        var next = await scenario.SignedInClientAsync();
        var taken = await HoldAsync(next, venue.Id, courts[0], 18);
        Assert.Equal(nameof(BookingStatus.Held), taken.Status);
    }

    [Fact]
    public async Task Giving_up_while_the_venue_is_still_looking_leaves_the_money_unsettled()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await WaitingBookingAsync(venue.Id, courts[0], 18);

        var offer = (await ReadAsync(booker, booking.Id)).Cancellation;
        Assert.True(offer.AwaitsVenue);
        Assert.Equal(100, offer.RefundPercent);
        // Nothing is owed yet, because nobody has said the money arrived (PRD 6.2).
        Assert.Equal(0m, offer.RefundBaht);

        var cancelled = await CancelAsync(booker, booking.Id);

        Assert.Equal(nameof(BookingStatus.Cancelled), cancelled.Status);
        Assert.Equal(nameof(PaymentState.Unconfirmed), cancelled.PaymentState);
        Assert.Equal(0m, cancelled.RefundDueBaht);
    }

    [Fact]
    public async Task Cancelling_a_confirmed_booking_in_good_time_gives_all_of_it_back()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        // The venue's default terms give everything back with a day's notice (PRD S-11).
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromHours(30));

        var offer = (await ReadAsync(booker, booking.Id)).Cancellation;
        Assert.Equal(100, offer.RefundPercent);
        Assert.Equal(booking.TotalBaht, offer.RefundBaht);

        var cancelled = await CancelAsync(booker, booking.Id);

        Assert.Equal(nameof(PaymentState.Received), cancelled.PaymentState);
        Assert.Equal(booking.TotalBaht, cancelled.RefundDueBaht);
        // Nothing has been sent back yet; recording that is US-18.
        Assert.Equal(0m, cancelled.RefundedBaht);
    }

    [Fact]
    public async Task Cancelling_too_late_for_the_terms_gives_nothing_back_but_still_frees_the_hours()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        // Inside the day's notice the default terms ask for, but not yet begun.
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromHours(2));

        var offer = (await ReadAsync(booker, booking.Id)).Cancellation;
        Assert.True(offer.Allowed);
        Assert.Equal(0, offer.RefundPercent);

        var cancelled = await CancelAsync(booker, booking.Id);

        Assert.Equal(nameof(BookingStatus.Cancelled), cancelled.Status);
        Assert.Equal(0m, cancelled.RefundDueBaht);
        // The venue keeps the money, and the court is still free for whoever wants it (PRD 6.1).
        Assert.Equal(nameof(PaymentState.Received), cancelled.PaymentState);
    }

    [Fact]
    public async Task Once_play_has_started_there_is_nothing_left_to_give_up()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);
        await scenario.PlayOutAsync(booking.Id);

        var refused = await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.PlayHasStarted, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_booking_the_venue_turned_away_cannot_be_cancelled()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/reject",
            new RejectSlipRequest("ยอดไม่ตรง", false));

        var refused = await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.NotCancellable, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Cancelling_twice_moves_the_booking_once()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await HoldAsync(booker, venue.Id, courts[0], 18);
        await CancelAsync(booker, booking.Id);

        var again = await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(BookingErrorCodes.NotCancellable, await again.ErrorCodeAsync());
        Assert.Single(await scenario.HistoryAsync(booking.Id), change => change.To == BookingStatus.Cancelled);
    }

    [Fact]
    public async Task Nobody_can_cancel_a_booking_that_is_not_theirs()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await HoldAsync(booker, venue.Id, courts[0], 18);
        var stranger = await scenario.SignedInClientAsync();

        var refused = await stranger.PostAsync($"/api/bookings/{booking.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.NotFound, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Who_gave_the_hours_up_and_when_is_in_the_history()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        var booking = await HoldAsync(booker, venue.Id, courts[0], 18);

        await CancelAsync(booker, booking.Id);

        var change = Assert.Single(
            await scenario.HistoryAsync(booking.Id),
            recorded => recorded.To == BookingStatus.Cancelled);
        Assert.Equal(BookingStatus.Held, change.From);
        // The booker pressed it, so the booker is named (PRD 6.1).
        Assert.NotNull(change.ChangedByUserId);
        Assert.Null(change.Reason);
    }

    private static Task<BookingResponse> HoldAsync(
        HttpClient client,
        Guid venueId,
        Guid courtId,
        int hour) =>
        VenueScenario.HoldAsync(client, venueId, VenueScenario.Today.AddDays(1), (courtId, hour));

    private static async Task<BookingHistoryResponse> MineAsync(HttpClient client) =>
        await VenueScenario.ReadAsync<BookingHistoryResponse>(
            await client.GetAsync("/api/bookings"));

    private static async Task<BookingResponse> ReadAsync(HttpClient client, Guid bookingId) =>
        await VenueScenario.ReadAsync<BookingResponse>(
            await client.GetAsync($"/api/bookings/{bookingId}"));

    private static async Task<BookingResponse> CancelAsync(HttpClient client, Guid bookingId) =>
        await VenueScenario.ReadAsync<BookingResponse>(
            await client.PostAsync($"/api/bookings/{bookingId}/cancel", null));

    /// <summary>A booking that has been paid for and is waiting for the venue to look at it.</summary>
    private async Task<(HttpClient Booker, BookingResponse Booking)> WaitingBookingAsync(
        Guid venueId,
        Guid courtId,
        int hour)
    {
        var booker = await scenario.SignedInClientAsync();
        var booking = await HoldAsync(booker, venueId, courtId, hour);

        var sent = await VenueScenario.UploadAsync(booker, booking.Id, VenueScenario.Jpeg());
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        return (booker, booking);
    }
}
