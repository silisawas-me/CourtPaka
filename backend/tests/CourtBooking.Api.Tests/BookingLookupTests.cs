using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>
/// The booking list's lookups (owner app, "รายการจอง"): a caller's booking found on any day by
/// name or phone, and one booking's story read back from the record.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BookingLookupTests(ApiTestFixture api)
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task A_booking_is_found_by_the_customer_name_or_phone_on_any_day()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var later = await Sell(owner, venue.Id, courts[0], VenueScenario.Today.AddDays(5), 19, "คุณปาล์ม", "0815550199");
        await Sell(owner, venue.Id, courts[0], VenueScenario.Today.AddDays(1), 20, "คุณต้น", null);

        var byName = await Find(owner, venue.Id, "ปาล์");
        Assert.Equal([later.BookingId], byName.Select(row => row.BookingId));

        var byPhone = await Find(owner, venue.Id, "555019");
        Assert.Equal([later.BookingId], byPhone.Select(row => row.BookingId));
    }

    [Fact]
    public async Task Too_little_typed_finds_nothing_rather_than_everybody()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        await Sell(owner, venue.Id, courts[0], VenueScenario.Today.AddDays(1), 19, "คุณต้น", null);

        Assert.Empty(await Find(owner, venue.Id, "ต"));
    }

    /// <summary>One venue's customers are not another's to look up (PDPA, US-13).</summary>
    [Fact]
    public async Task Another_venue_bookings_are_not_found()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        await Sell(owner, venue.Id, courts[0], VenueScenario.Today.AddDays(1), 19, "คุณมิ้นท์ลับ", null);
        var (other, otherVenue, _) = await scenario.BookableVenueAsync();

        Assert.Empty(await Find(other, otherVenue.Id, "มิ้นท์ลับ"));
    }

    /// <summary>The list names people, so it is behind the same permission as the day's list.</summary>
    [Fact]
    public async Task Staff_without_manage_bookings_cannot_look()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var staff = await scenario.StaffClientAsync(owner, venue.Id, nameof(VenuePermissions.ViewReports));

        var refused = await staff.GetAsync($"/api/venues/{venue.Id}/bookings/find?q=abc");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task The_history_says_what_happened_in_order_and_who_did_it()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var paid = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/payments",
            new TakePaymentRequest(booking.TotalBaht, nameof(PaymentMethod.Cash), null));
        Assert.True(paid.IsSuccessStatusCode);

        var history = await VenueScenario.ReadAsync<BookingHistoryEntryResponse[]>(
            await owner.GetAsync($"/api/venues/{venue.Id}/bookings/{booking.Id}/history"));

        // Held, then waiting on the slip, then paid for; the money itself is a line of its own.
        Assert.Equal("Held", history[0].To);
        Assert.Equal(["Held", "PendingVerification", "Confirmed"],
            history.Where(entry => entry.Kind == nameof(BookingHistoryKind.Status)).Select(entry => entry.To));
        Assert.True(history.Zip(history.Skip(1)).All(pair => pair.First.At <= pair.Second.At));
        var payment = Assert.Single(history, entry => entry.Kind == nameof(BookingHistoryKind.Payment));
        Assert.Equal(booking.TotalBaht, payment.AmountBaht);
        Assert.Equal(nameof(PaymentMethod.Cash), payment.Method);
        Assert.NotNull(payment.By);
    }

    [Fact]
    public async Task Another_venue_booking_has_no_history_here()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var sold = await Sell(owner, venue.Id, courts[0], VenueScenario.Today.AddDays(1), 19, "คุณต้น", null);
        var (other, otherVenue, _) = await scenario.BookableVenueAsync();

        var refused = await other.GetAsync($"/api/venues/{otherVenue.Id}/bookings/{sold.BookingId}/history");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    private static async Task<VenueBookingResponse> Sell(
        HttpClient owner, Guid venueId, Guid courtId, DateOnly day, int hour, string name, string? phone) =>
        await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/bookings",
                new CounterBookingRequest(
                    [new BookingSlotRequest(courtId, day, hour)], name, phone, nameof(CounterPayment.Cash))),
            HttpStatusCode.Created);

    private static async Task<VenueBookingResponse[]> Find(HttpClient client, Guid venueId, string q) =>
        await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await client.GetAsync($"/api/venues/{venueId}/bookings/find?q={Uri.EscapeDataString(q)}"));
}
