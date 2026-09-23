using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Jobs;
using CourtBooking.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>
/// Hours that came back, offered to whoever asked for them first (PRD US-27). The offer is an
/// ordinary hold — the same fifteen minutes, the same way of paying, the same constraint behind
/// it — so what is tested here is who gets offered what, and what happens when they do not answer.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class WaitlistOfferTests(ApiTestFixture api)
{
    private readonly VenueScenario scenario = new(api);

    /// <summary>
    /// Far enough ahead that the other suites are not booking into it, and inside the window a
    /// queue may be joined for.
    /// </summary>
    private static readonly DateOnly Day = VenueScenario.Today.AddDays(25);

    [Fact]
    public async Task An_hour_that_came_free_is_held_for_whoever_asked_first()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var first = await scenario.SignedInClientAsync();
        var second = await scenario.SignedInClientAsync();

        await JoinAsync(first, venue.Id, 18, 20, 1);
        await JoinAsync(second, venue.Id, 18, 20, 1);

        await OfferAsync();

        // One offer, to the one who asked first, as an ordinary hold on their own name.
        var offers = await OffersAsync(venue.Id);
        var offer = Assert.Single(offers);
        Assert.Equal(WaitlistState.Offered, offer.State);

        var held = await BookingAsync(offer.OfferedBookingId!.Value);
        Assert.Equal(BookingStatus.Held, held.Status);
        Assert.Equal(await MineAsync(first), held.BookerUserId);

        // The hours are inside the window that was asked for, and on one court.
        Assert.All(held.Slots, slot => Assert.Contains(slot.CourtId, courts));
        Assert.Single(held.Slots);
    }

    /// <summary>
    /// An offer nobody answers ends, and the next sweep gives the hours to whoever is behind
    /// them — which is the whole point of a queue (PRD US-27).
    /// </summary>
    [Fact]
    public async Task An_offer_nobody_answers_goes_to_the_next_person()
    {
        var (_, venue, _) = await scenario.BookableVenueAsync();
        var first = await scenario.SignedInClientAsync();
        var second = await scenario.SignedInClientAsync();
        var firstId = await MineAsync(first);
        var secondId = await MineAsync(second);

        await JoinAsync(first, venue.Id, 18, 20, 1);
        await JoinAsync(second, venue.Id, 18, 20, 1);

        await OfferAsync();
        var offered = Assert.Single(await OffersAsync(venue.Id));
        Assert.Equal(firstId, (await BookingAsync(offered.OfferedBookingId!.Value)).BookerUserId);

        // Nobody answers it.
        await scenario.LapseHoldAsync(offered.OfferedBookingId!.Value);
        await OfferAsync();

        var after = await EntriesAsync(venue.Id);
        Assert.Equal(WaitlistState.Gone, after.Single(entry => entry.BookerUserId == firstId).State);

        var next = after.Single(entry => entry.BookerUserId == secondId);
        Assert.Equal(WaitlistState.Offered, next.State);
        Assert.Equal(secondId, (await BookingAsync(next.OfferedBookingId!.Value)).BookerUserId);
    }

    /// <summary>Taking the offer up is an ordinary payment, and it ends the place in the queue.</summary>
    [Fact]
    public async Task Taking_the_offer_up_is_the_ordinary_way_of_paying_for_it()
    {
        var (_, venue, _) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();
        await JoinAsync(booker, venue.Id, 18, 20, 1);

        await OfferAsync();
        var offer = Assert.Single(await OffersAsync(venue.Id));

        var sent = await VenueScenario.UploadAsync(
            booker, offer.OfferedBookingId!.Value, VenueScenario.Jpeg());
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        await OfferAsync();

        var entry = Assert.Single(await EntriesAsync(venue.Id));
        Assert.Equal(WaitlistState.Taken, entry.State);
        Assert.NotNull(entry.EndedAt);
    }

    /// <summary>
    /// Nothing is offered that the day does not have. A window with no free run in it leaves the
    /// place in the queue exactly where it was, waiting.
    /// </summary>
    [Fact]
    public async Task A_want_the_day_cannot_answer_is_left_waiting()
    {
        var (_, venue, _) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();

        // Two hours after the venue shuts. The queue takes the want — somebody may open later —
        // but no day as it stands can answer it.
        await JoinAsync(booker, venue.Id, 22, 24, 2);

        await OfferAsync();

        var entry = Assert.Single(await EntriesAsync(venue.Id));
        Assert.Equal(WaitlistState.Waiting, entry.State);
        Assert.Null(entry.OfferedBookingId);
    }

    /// <summary>
    /// Somebody already holding hours is passed over rather than refused: one hold at a time
    /// (PRD S-22), and their place stays for the next sweep.
    /// </summary>
    [Fact]
    public async Task Somebody_already_holding_hours_keeps_their_place_and_waits()
    {
        var (_, venue, courts) = await scenario.BookableVenueAsync();
        var booker = await scenario.SignedInClientAsync();

        await JoinAsync(booker, venue.Id, 18, 20, 1);
        // A hold of their own, somewhere else entirely.
        await VenueScenario.HoldAsync(booker, venue.Id, Day.AddDays(1), (courts[0], 8));

        await OfferAsync();

        var entry = Assert.Single(await EntriesAsync(venue.Id));
        Assert.Equal(WaitlistState.Waiting, entry.State);
    }

    private async Task JoinAsync(HttpClient booker, Guid venueId, int from, int until, int hours)
    {
        var joined = await booker.PostAsJsonAsync(
            "/api/waitlist", new JoinWaitlistRequest(venueId, Day, from, until, hours));

        if (joined.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException(
                $"Could not join the queue: {joined.StatusCode} {await joined.ErrorCodeAsync()}");
        }
    }

    private async Task OfferAsync()
    {
        using var scope = api.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WaitlistOffers>()
            .WorkAsync(CancellationToken.None);
    }

    private async Task<WaitlistEntry[]> EntriesAsync(Guid venueId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.WaitlistEntries
            .AsNoTracking()
            .Where(entry => entry.VenueId == venueId && entry.Date == Day)
            .ToArrayAsync();
    }

    private async Task<WaitlistEntry[]> OffersAsync(Guid venueId) =>
        [.. (await EntriesAsync(venueId)).Where(entry => entry.State == WaitlistState.Offered)];

    private async Task<Booking> BookingAsync(Guid bookingId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await database.Bookings
            .AsNoTracking()
            .Include(booking => booking.Slots)
            .SingleAsync(booking => booking.Id == bookingId);
    }

    /// <summary>The id behind a signed-in client, which is what the entries are keyed by.</summary>
    private static async Task<Guid> MineAsync(HttpClient client) =>
        (await VenueScenario.ReadAsync<CurrentUserResponse>(
            await client.GetAsync("/api/auth/me"))).Id;
}
