using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>
/// What the owner app's revenue page reads off the dashboard (docs/plan/owner-app.md PR-5): the
/// shop's money day by day, the money by how it was paid, the best sellers, and the period
/// before — each by the rules the dashboard already keeps.
/// </summary>
public sealed class DashboardRevenueTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    private static string Today => VenueScenario.Today.ToString("yyyy-MM-dd");

    private static async Task<ShopItemResponse> AddAsync(
        HttpClient owner, Guid venueId, string name, decimal price) =>
        await VenueScenario.ReadAsync<ShopItemResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/shop/items",
                new ShopItemRequest(name, price, "ชิ้น", false, null)),
            HttpStatusCode.Created);

    private static async Task<ShopSaleResponse> SellAsync(
        HttpClient owner, Guid venueId, Guid itemId, int quantity) =>
        await VenueScenario.ReadAsync<ShopSaleResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/shop/sales",
                new ShopSaleRequest(
                    [new ShopSaleLineRequest(itemId, quantity)], nameof(PaymentMethod.Cash), null)),
            HttpStatusCode.Created);

    private static async Task<DashboardResponse> DashboardAsync(HttpClient owner, Guid venueId) =>
        await VenueScenario.ReadAsync<DashboardResponse>(
            await owner.GetAsync($"/api/venues/{venueId}/dashboard?from={Today}&to={Today}"),
            HttpStatusCode.OK);

    [Fact]
    public async Task The_shop_is_counted_by_the_day_and_its_best_sellers_named()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var water = await AddAsync(owner, venue.Id, "น้ำดื่ม", 20m);
        var racquet = await AddAsync(owner, venue.Id, "เช่าไม้", 50m);

        await SellAsync(owner, venue.Id, water.ItemId, 3);
        await SellAsync(owner, venue.Id, racquet.ItemId, 1);
        // Sold and handed back the same day: in and out on that day, and not a best seller.
        var returned = await SellAsync(owner, venue.Id, water.ItemId, 1);
        (await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/shop/sales/{returned.SaleId}/cancel",
            new { reason = "ผิดรายการ" })).EnsureSuccessStatusCode();

        var dashboard = await DashboardAsync(owner, venue.Id);

        // The day's shop money is the range's shop money, split by day — the two cannot disagree.
        var today = Assert.Single(dashboard.Days);
        Assert.Equal(110m, today.ShopBaht);
        Assert.Equal(dashboard.Trade.ShopBaht, dashboard.Days.Sum(day => day.ShopBaht));

        Assert.Equal(
            [("น้ำดื่ม", 3, 60m), ("เช่าไม้", 1, 50m)],
            dashboard.TopItems.Select(item => (item.Name, item.Quantity, item.Baht)));
    }

    /// <summary>
    /// Money by how it came in, from the receipts — every one counted on these days, whatever it
    /// was for. The sale handed back still came in; its going out is the drawer's other column.
    /// </summary>
    [Fact]
    public async Task Money_is_told_by_how_it_was_paid()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var water = await AddAsync(owner, venue.Id, "น้ำดื่ม", 20m);
        await SellAsync(owner, venue.Id, water.ItemId, 2);

        var dashboard = await DashboardAsync(owner, venue.Id);

        var cash = Assert.Single(dashboard.ByMethod);
        Assert.Equal(nameof(PaymentMethod.Cash), cash.Method);
        Assert.Equal(40m, cash.Baht);
    }

    [Fact]
    public async Task A_new_venue_has_nothing_in_the_period_before()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        Assert.Equal(0m, (await DashboardAsync(owner, venue.Id)).PriorBaht);
    }
}
