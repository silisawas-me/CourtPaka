using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Documents;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>A booking's receipt, previewed (thai-fit T6): which paper, what is on it, the VAT.</summary>
public sealed class ReceiptPreviewTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public void Vat_is_seven_of_every_hundred_and_seven_and_the_two_add_back()
    {
        // The artboard's full invoice: ฿1,490 paid is ฿1,392.52 and ฿97.48 of VAT.
        Assert.Equal((1_392.52m, 97.48m), ReceiptPreview.SplitVat(1_490m));
        Assert.Equal((0m, 0m), ReceiptPreview.SplitVat(0m));
        var (before, vat) = ReceiptPreview.SplitVat(333.33m);
        Assert.Equal(333.33m, before + vat);
    }

    [Fact]
    public async Task A_vat_registered_venue_gives_the_abbreviated_invoice_with_its_courts_and_its_shop()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booking = await SellTwoHoursAsync(owner, venue.Id, courts[0], 18);
        var item = await AddItemAsync(owner, venue.Id);
        await SellItemAsync(owner, venue.Id, item.ItemId, 3, booking.BookingId);

        var receipt = await ReceiptAsync(owner, venue.Id, booking.BookingId);

        Assert.Equal(nameof(DocumentKind.Abb), receipt.Kind);
        Assert.StartsWith($"{venue.Code}-ABB-", receipt.NumberPrefix);
        Assert.True(receipt.Seller.VatRegistered);
        Assert.Equal("0105561000000", receipt.Seller.TaxId);
        Assert.Equal("ลูกค้าใบเสร็จ", receipt.CustomerName);

        var court = Assert.Single(receipt.Lines, line => line.Kind == nameof(ReceiptLineKind.Court));
        Assert.Equal(2, court.Quantity);
        Assert.Equal(400m, court.AmountBaht);
        Assert.Equal(TimeSpan.FromHours(2), court.EndsAt - court.StartsAt);
        var shop = Assert.Single(receipt.Lines, line => line.Kind == nameof(ReceiptLineKind.Item));
        Assert.Equal(3, shop.Quantity);
        Assert.Equal(60m, shop.AmountBaht);

        Assert.Equal(460m, receipt.TotalBaht);
        Assert.Equal(ReceiptPreview.SplitVat(460m), (receipt.BeforeVatBaht, receipt.VatBaht));
        Assert.Contains(nameof(PaymentMethod.Cash), receipt.PaidBy);
    }

    [Fact]
    public async Task A_venue_not_registered_gives_a_receipt_with_no_vat_in_it()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        await NotVatRegisteredAsync(venue.Id);
        var booking = await SellTwoHoursAsync(owner, venue.Id, courts[0], 18);

        var receipt = await ReceiptAsync(owner, venue.Id, booking.BookingId);

        Assert.Equal(nameof(DocumentKind.Rec), receipt.Kind);
        Assert.False(receipt.Seller.VatRegistered);
        Assert.Equal(0m, receipt.VatBaht);
        Assert.Equal(receipt.TotalBaht, receipt.BeforeVatBaht);
    }

    [Fact]
    public async Task A_sale_taken_back_is_not_on_the_receipt()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booking = await SellTwoHoursAsync(owner, venue.Id, courts[0], 18);
        var item = await AddItemAsync(owner, venue.Id);
        var sale = await SellItemAsync(owner, venue.Id, item.ItemId, 1, booking.BookingId);
        var takenBack = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/shop/sales/{sale.SaleId}/cancel", new ShopSaleCancelRequest("คีย์ผิด"));
        Assert.Equal(HttpStatusCode.OK, takenBack.StatusCode);

        var receipt = await ReceiptAsync(owner, venue.Id, booking.BookingId);

        Assert.DoesNotContain(receipt.Lines, line => line.Kind == nameof(ReceiptLineKind.Item));
        Assert.Equal(400m, receipt.TotalBaht);
    }

    [Fact]
    public async Task It_takes_no_number_from_the_sequence_that_may_not_skip()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booking = await SellTwoHoursAsync(owner, venue.Id, courts[0], 18);

        await ReceiptAsync(owner, venue.Id, booking.BookingId);
        await ReceiptAsync(owner, venue.Id, booking.BookingId);

        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await database.DocumentSeries.AnyAsync(series => series.SeriesCode == venue.Code));
    }

    [Fact]
    public async Task Staff_without_bookings_do_not_read_it_and_another_venue_finds_nothing()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var booking = await SellTwoHoursAsync(owner, venue.Id, courts[0], 18);
        var staff = await scenario.StaffClientAsync(owner, venue.Id, nameof(VenuePermissions.CloseCourt));

        var refused = await staff.GetAsync($"/api/venues/{venue.Id}/bookings/{booking.BookingId}/receipt");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var (other, otherVenue, _) = await scenario.BookableVenueAsync();
        var elsewhere = await other.GetAsync($"/api/venues/{otherVenue.Id}/bookings/{booking.BookingId}/receipt");
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
    }

    private static async Task<VenueBookingResponse> SellTwoHoursAsync(
        HttpClient owner, Guid venueId, Guid courtId, int hour)
    {
        var day = VenueScenario.Today.AddDays(1);
        return await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/bookings",
                new
                {
                    slots = new[]
                    {
                        new { courtId, date = day, hour },
                        new { courtId, date = day, hour = hour + 1 },
                    },
                    customerName = "ลูกค้าใบเสร็จ",
                    customerPhone = (string?)null,
                    paidBy = "Cash",
                }),
            HttpStatusCode.Created);
    }

    private static async Task<ShopItemResponse> AddItemAsync(HttpClient owner, Guid venueId) =>
        await VenueScenario.ReadAsync<ShopItemResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/shop/items",
                new ShopItemRequest("น้ำดื่ม", 20m, "ขวด", false, null)),
            HttpStatusCode.Created);

    private static async Task<ShopSaleResponse> SellItemAsync(
        HttpClient owner, Guid venueId, Guid itemId, int quantity, Guid bookingId) =>
        await VenueScenario.ReadAsync<ShopSaleResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/shop/sales",
                new ShopSaleRequest(
                    [new ShopSaleLineRequest(itemId, quantity)], nameof(PaymentMethod.Cash), bookingId)),
            HttpStatusCode.Created);

    private static async Task<ReceiptPreviewResponse> ReceiptAsync(HttpClient owner, Guid venueId, Guid bookingId) =>
        await VenueScenario.ReadAsync<ReceiptPreviewResponse>(
            await owner.GetAsync($"/api/venues/{venueId}/bookings/{bookingId}/receipt"));

    private async Task NotVatRegisteredAsync(Guid venueId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var venue = await database.Venues.SingleAsync(one => one.Id == venueId);
        venue.Business.IsVatRegistered = false;
        await database.SaveChangesAsync();
    }
}
