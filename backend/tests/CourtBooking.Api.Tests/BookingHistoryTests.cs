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
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var offer = (await ReadAsync(booker, booking.Id)).Cancellation;
        Assert.True(offer.AwaitsVenue);
        Assert.Equal(100, offer.RefundPercent);
        // Nothing is owed yet, because nobody has said the money arrived (PRD 6.2).
        Assert.Equal(0m, offer.RefundBaht);

        var cancelled = await CancelAsync(booker, booking.Id);

        Assert.Equal(nameof(BookingStatus.Cancelled), cancelled.Status);
        Assert.Equal(nameof(PaymentState.Unconfirmed), cancelled.PaymentState);
        Assert.Equal(0m, cancelled.RefundDueBaht);

        // The share is written down even though nothing is owed yet: when the venue says the
        // money did arrive (US-13), it is this that decides the amount (PRD 6.2).
        Assert.Equal(100, (await scenario.StoredBookingAsync(booking.Id)).RefundPercent);

        // And the hours are somebody else's to take (PRD 6.1).
        Assert.False(await scenario.HoldsItsHoursAsync(booking.Id));
        var next = await scenario.SignedInClientAsync();
        Assert.Equal(
            nameof(BookingStatus.Held),
            (await HoldAsync(next, venue.Id, courts[0], 18)).Status);
    }

    [Fact]
    public async Task The_venue_cannot_decide_a_booking_the_booker_has_already_let_go()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await CancelAsync(booker, booking.Id);

        var refused = await owner.PostAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        // Confirming here would leave a booking that says it holds hours it has already released,
        // which is a court sold twice over (PRD BR-04, 9.2).
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(SlipErrorCodes.NotAwaitingVerification, await refused.ErrorCodeAsync());
        Assert.False(await scenario.HoldsItsHoursAsync(booking.Id));
    }

    [Fact]
    public async Task What_is_coming_up_is_ordered_by_when_it_is_played()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync(courts: 2);
        var booker = await scenario.SignedInClientAsync();

        // Taken in the opposite order to the order they will be played in, which is the ordinary
        // case: somebody books the weekend first and then remembers tomorrow.
        var later = await HoldAsync(booker, venue.Id, courts[0], 20);
        await scenario.LapseHoldAsync(later.Id);
        var sooner = await HoldAsync(booker, venue.Id, courts[1], 18);

        var upcoming = (await MineAsync(booker)).Upcoming;

        // The lapsed one is behind them, so what is left is ordered by its hours.
        Assert.Equal(sooner.Id, upcoming[0].Id);
    }

    [Fact]
    public async Task Cancelling_a_confirmed_booking_in_good_time_gives_all_of_it_back()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
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
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
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
        Assert.False(await scenario.HoldsItsHoursAsync(booking.Id));
    }

    [Fact]
    public async Task Once_play_has_started_there_is_nothing_left_to_give_up()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);

        // Half an hour into an hour: begun, but not over.
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(-30));

        var refused = await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BookingErrorCodes.PlayHasStarted, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task Hours_that_have_been_played_read_as_played_without_waiting_for_a_job()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{booking.Id}/confirm", null);
        await scenario.PlayOutAsync(booking.Id);

        // Nothing has run to write this down, and every read says it anyway (PRD 9.2).
        var played = Assert.Single((await MineAsync(booker)).Past);
        Assert.Equal(booking.Id, played.Id);
        Assert.Equal(nameof(BookingStatus.Completed), played.Status);
        Assert.False(played.Cancellation.Allowed);

        var refused = await booker.PostAsync($"/api/bookings/{booking.Id}/cancel", null);
        Assert.Equal(BookingErrorCodes.NotCancellable, await refused.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_booking_the_venue_turned_away_cannot_be_cancelled()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
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

}
