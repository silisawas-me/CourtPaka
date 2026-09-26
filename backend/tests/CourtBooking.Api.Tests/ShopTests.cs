using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>
/// What a counter sells besides court time, and what the venue paid out (PRD US-32, US-33). What
/// is tested here is that the money lands in the same till as everything else, that the stock is
/// a ledger rather than a number, and that nothing is ever sold twice or paid out twice.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ShopTests(ApiTestFixture api)
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task A_line_goes_on_the_board_and_says_how_many_are_left()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        var shuttles = await AddAsync(owner, venue.Id);
        Assert.Equal(90m, shuttles.PriceBaht);
        Assert.True(shuttles.Counted);
        Assert.Equal(0, shuttles.Left);

        // What is not counted has no number, rather than a zero that reads as none left.
        var racquet = await AddAsync(
            owner, venue.Id, name: "เช่าไม้", price: 50m, unit: "ชั่วโมง", counted: false);
        Assert.Null(racquet.Left);
        Assert.False(racquet.RunningLow);
    }

    /// <summary>
    /// Selling is money in the till on the day it happens, counted with everything else the venue
    /// took (PRD US-26) — and the stock comes off in the same write.
    /// </summary>
    [Fact]
    public async Task Selling_takes_the_money_and_the_stock_together()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var item = await AddAsync(owner, venue.Id);
        await BuyInAsync(owner, venue.Id, item.ItemId, 10, 700m);

        var before = await MoneyAsync(owner, venue.Id);
        var sale = await SellAsync(owner, venue.Id, (item.ItemId, 2));

        Assert.Equal(180m, sale.TotalBaht);
        var line = Assert.Single(sale.Lines);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(90m, line.EachBaht);

        // Eight left of the ten that were bought in.
        Assert.Equal(8, (await BoardAsync(owner, venue.Id)).Single(one => one.ItemId == item.ItemId).Left);

        // And the day's till has it, as a row that names the sale rather than a booking.
        var after = await MoneyAsync(owner, venue.Id);
        Assert.Equal(before.CashBaht + 180m, after.CashBaht);
        var receipt = Assert.Single(after.CashReceipts, one => one.SaleId == sale.SaleId);
        Assert.Null(receipt.BookingId);
        Assert.Null(receipt.PackageId);
    }

    /// <summary>Nothing is sold that is not there (PRD US-32).</summary>
    [Fact]
    public async Task What_is_not_on_the_shelf_is_not_sold()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var item = await AddAsync(owner, venue.Id);
        await BuyInAsync(owner, venue.Id, item.ItemId, 1, 70m);

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/shop/sales",
            new ShopSaleRequest(
                [new ShopSaleLineRequest(item.ItemId, 2)], nameof(PaymentMethod.Cash), null));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(ShopErrorCodes.NotEnoughStock, await refused.ErrorCodeAsync());

        // And nothing came off the shelf.
        Assert.Equal(1, (await BoardAsync(owner, venue.Id)).Single(one => one.ItemId == item.ItemId).Left);
    }

    /// <summary>What has no number cannot run short of one.</summary>
    [Fact]
    public async Task What_is_not_counted_is_always_there_to_sell()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var racquet = await AddAsync(
            owner, venue.Id, name: "เช่าไม้", price: 50m, unit: "ชั่วโมง", counted: false);

        var sale = await SellAsync(owner, venue.Id, (racquet.ItemId, 4));
        Assert.Equal(200m, sale.TotalBaht);
        Assert.Null((await BoardAsync(owner, venue.Id)).Single(one => one.ItemId == racquet.ItemId).Left);
    }

    /// <summary>
    /// Taking a sale back puts the stock on the shelf and writes down the money going out. Nothing
    /// is deleted: what happened happened (PRD US-32).
    /// </summary>
    [Fact]
    public async Task Taking_a_sale_back_returns_the_stock_and_records_the_money_going_out()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var item = await AddAsync(owner, venue.Id);
        await BuyInAsync(owner, venue.Id, item.ItemId, 10, 700m);
        var sale = await SellAsync(owner, venue.Id, (item.ItemId, 3));

        var cancelled = await VenueScenario.ReadAsync<ShopSaleResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/shop/sales/{sale.SaleId}/cancel",
                new ShopSaleCancelRequest("ลูกค้าคืน")));

        Assert.NotNull(cancelled.CancelledAt);
        Assert.Equal("ลูกค้าคืน", cancelled.CancelReason);
        Assert.Equal(10, (await BoardAsync(owner, venue.Id)).Single(one => one.ItemId == item.ItemId).Left);

        // The money going back out is written where money going out is written.
        var spending = await SpendingAsync(owner, venue.Id);
        Assert.Contains(spending, one => one.AmountBaht == 270m && one.VoidedAt is null);

        // Once, and only once.
        var again = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/shop/sales/{sale.SaleId}/cancel",
            new ShopSaleCancelRequest("อีกที"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(ShopErrorCodes.SaleAlreadyCancelled, await again.ErrorCodeAsync());
    }

    /// <summary>
    /// Buying stock is one expense that also puts something on the shelf — both rows in one
    /// write, because a venue that had to do it twice would sooner or later do it once (US-33).
    /// </summary>
    [Fact]
    public async Task Buying_stock_is_one_expense_and_one_movement()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var item = await AddAsync(owner, venue.Id);

        var spend = await BuyInAsync(owner, venue.Id, item.ItemId, 12, 840m);
        Assert.Equal(nameof(SpendKind.Stock), spend.Kind);
        Assert.Equal(840m, spend.AmountBaht);

        Assert.Equal(12, (await BoardAsync(owner, venue.Id)).Single(one => one.ItemId == item.ItemId).Left);
    }

    /// <summary>Half of a purchase is a row that looks like one and moves nothing.</summary>
    [Fact]
    public async Task A_purchase_says_both_what_and_how_many_or_neither()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var item = await AddAsync(owner, venue.Id);

        var halfway = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/shop/spending",
            new SpendRequest(
                nameof(SpendKind.Stock), 500m, null, nameof(PaymentMethod.Cash), null,
                item.ItemId, null));

        Assert.Equal(HttpStatusCode.BadRequest, halfway.StatusCode);
        Assert.Equal(SpendErrorCodes.NotAStockPurchase, await halfway.ErrorCodeAsync());

        // And a thing bought under the wrong heading is not a purchase either.
        var mislabelled = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/shop/spending",
            new SpendRequest(
                nameof(SpendKind.Wages), 500m, null, nameof(PaymentMethod.Cash), null,
                item.ItemId, 5));

        Assert.Equal(HttpStatusCode.BadRequest, mislabelled.StatusCode);
        Assert.Equal(SpendErrorCodes.NotAStockPurchase, await mislabelled.ErrorCodeAsync());
    }

    /// <summary>
    /// Cash paid out is cash that is no longer in the drawer, so the count has to know about it
    /// or the till never balances (PRD US-26, US-33).
    /// </summary>
    [Fact]
    public async Task Cash_paid_out_comes_out_of_the_till()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var item = await AddAsync(owner, venue.Id);
        await BuyInAsync(owner, venue.Id, item.ItemId, 10, 0.01m, PaymentMethod.PromptPay);
        await SellAsync(owner, venue.Id, (item.ItemId, 2));

        var before = await MoneyAsync(owner, venue.Id);

        // Paid out of the drawer, in cash.
        await VenueScenario.ReadAsync<SpendResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/shop/spending",
                new SpendRequest(
                    nameof(SpendKind.Utilities), 60m, null, nameof(PaymentMethod.Cash),
                    "ค่าน้ำ", null, null)),
            HttpStatusCode.Created);

        var after = await MoneyAsync(owner, venue.Id);
        Assert.Equal(before.CashRefundedBaht + 60m, after.CashRefundedBaht);

        // Which is what the count expects to find.
        var counted = await VenueScenario.ReadAsync<DailyClosingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/money/closing",
                new CloseDayRequest(0m, after.CashBaht - after.CashRefundedBaht, null)));

        Assert.Equal(0m, counted.DifferenceBaht);
    }

    /// <summary>A record of money paid out is voided, never changed (PRD US-33, as US-18).</summary>
    [Fact]
    public async Task A_record_of_money_paid_out_is_voided_rather_than_changed()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        var spend = await VenueScenario.ReadAsync<SpendResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/shop/spending",
                new SpendRequest(
                    nameof(SpendKind.Repairs), 1_200m, null, nameof(PaymentMethod.PromptPay),
                    "ซ่อมไฟ", null, null)),
            HttpStatusCode.Created);

        var voided = await VenueScenario.ReadAsync<SpendResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/shop/spending/{spend.SpendId}/void",
                new ShopSaleCancelRequest("คีย์ผิด")));

        Assert.NotNull(voided.VoidedAt);
        Assert.Equal("คีย์ผิด", voided.VoidReason);
        Assert.Equal(1_200m, voided.AmountBaht);

        var again = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/shop/spending/{spend.SpendId}/void",
            new ShopSaleCancelRequest("อีกที"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(SpendErrorCodes.AlreadyVoided, await again.ErrorCodeAsync());
    }

    /// <summary>
    /// Counting the shelf is the one movement with no money beside it, and the only one that has
    /// to say why (PRD US-33).
    /// </summary>
    [Fact]
    public async Task Counting_the_shelf_writes_down_the_difference_and_why()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var item = await AddAsync(owner, venue.Id);
        await BuyInAsync(owner, venue.Id, item.ItemId, 10, 700m);

        var counted = await VenueScenario.ReadAsync<ShopItemResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/shop/items/{item.ItemId}/count",
                new StockCountRequest(7, "นับได้เท่านี้")));

        Assert.Equal(7, counted.Left);

        var without = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/shop/items/{item.ItemId}/count",
            new StockCountRequest(7, null));
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
    }

    /// <summary>Few enough left that somebody should be told (PRD US-33).</summary>
    [Fact]
    public async Task It_says_when_there_are_not_many_left()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var item = await AddAsync(owner, venue.Id, tellMeAt: 3);
        await BuyInAsync(owner, venue.Id, item.ItemId, 10, 700m);

        Assert.False((await BoardAsync(owner, venue.Id)).Single(one => one.ItemId == item.ItemId).RunningLow);

        await SellAsync(owner, venue.Id, (item.ItemId, 7));

        Assert.True((await BoardAsync(owner, venue.Id)).Single(one => one.ItemId == item.ItemId).RunningLow);
    }

    /// <summary>
    /// The shop's money is kept apart from the court's: a different business with a different
    /// margin, and a venue that cannot tell them apart cannot tell whether either works (US-32).
    /// </summary>
    [Fact]
    public async Task The_shop_and_the_courts_are_counted_apart()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var before = await DashboardAsync(owner, venue.Id);

        var item = await AddAsync(owner, venue.Id);
        await BuyInAsync(owner, venue.Id, item.ItemId, 10, 700m);
        var sale = await SellAsync(owner, venue.Id, (item.ItemId, 2));

        var after = await DashboardAsync(owner, venue.Id);
        Assert.Equal(before.Trade.ShopBaht + 180m, after.Trade.ShopBaht);
        Assert.Equal(before.Trade.SpentBaht + 700m, after.Trade.SpentBaht);

        // Court money is untouched by any of it.
        Assert.Equal(before.OnlineBaht, after.OnlineBaht);
        Assert.Equal(before.StaffBaht, after.StaffBaht);

        // And a sale that was taken back stops counting.
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/shop/sales/{sale.SaleId}/cancel",
                new ShopSaleCancelRequest("คืน"))).StatusCode);

        Assert.Equal(before.Trade.ShopBaht, (await DashboardAsync(owner, venue.Id)).Trade.ShopBaht);
    }

    /// <summary>
    /// The board carries prices and the sales carry money, so both sit behind the permission the
    /// rest of the counter's money sits behind (PRD US-13, US-32).
    /// </summary>
    [Fact]
    public async Task Somebody_without_the_permission_cannot_read_or_sell()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.VerifySlip));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await staff.GetAsync($"/api/venues/{venue.Id}/shop/sales")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await staff.GetAsync($"/api/venues/{venue.Id}/shop/spending")).StatusCode);
    }

    /// <summary>Selling is selling, and a suspension stops it (PRD US-20).</summary>
    [Fact]
    public async Task A_suspended_venue_cannot_sell_but_can_still_write_down_what_it_paid()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var item = await AddAsync(owner, venue.Id);
        await BuyInAsync(owner, venue.Id, item.ItemId, 10, 700m);

        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/shop/sales",
                new ShopSaleRequest(
                    [new ShopSaleLineRequest(item.ItemId, 1)],
                    nameof(PaymentMethod.Cash),
                    null))).StatusCode);

        // Money that already went out still has to be written down and read.
        Assert.Equal(
            HttpStatusCode.Created,
            (await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/shop/spending",
                new SpendRequest(
                    nameof(SpendKind.Utilities), 90m, null, nameof(PaymentMethod.Cash),
                    null, null, null))).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.GetAsync($"/api/venues/{venue.Id}/shop/sales")).StatusCode);
    }

    /// <summary>A sale may sit beside a booking, and it has to be one of this venue's.</summary>
    [Fact]
    public async Task A_sale_beside_a_booking_names_one_of_this_venues()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var item = await AddAsync(owner, venue.Id);
        await BuyInAsync(owner, venue.Id, item.ItemId, 10, 700m);

        var (_, booking) = await scenario.ConfirmedBookingAsync(owner, venue.Id, courts[0], 18);
        var sale = await SellAsync(owner, venue.Id, (item.ItemId, 1), booking.Id);
        Assert.Equal(booking.Id, sale.BookingId);

        var (elsewhereOwner, elsewhere, otherCourts) = await scenario.BookableVenueAsync();
        var (_, theirs) = await scenario.ConfirmedBookingAsync(
            elsewhereOwner, elsewhere.Id, otherCourts[0], 18);

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/shop/sales",
            new ShopSaleRequest(
                [new ShopSaleLineRequest(item.ItemId, 1)], nameof(PaymentMethod.Cash), theirs.Id));

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(ShopErrorCodes.BookingUnknown, await refused.ErrorCodeAsync());
    }

    private static async Task<ShopItemResponse> AddAsync(
        HttpClient owner,
        Guid venueId,
        string name = "ลูกขนไก่",
        decimal price = 90m,
        string unit = "ลูก",
        bool counted = true,
        int? tellMeAt = null) =>
        await VenueScenario.ReadAsync<ShopItemResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/shop/items",
                new ShopItemRequest(name, price, unit, counted, tellMeAt)),
            HttpStatusCode.Created);

    private static async Task<SpendResponse> BuyInAsync(
        HttpClient owner,
        Guid venueId,
        Guid itemId,
        int quantity,
        decimal amount,
        PaymentMethod paidBy = PaymentMethod.PromptPay) =>
        await VenueScenario.ReadAsync<SpendResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/shop/spending",
                new SpendRequest(
                    nameof(SpendKind.Stock), amount, null, paidBy.ToString(), null,
                    itemId, quantity)),
            HttpStatusCode.Created);

    private static async Task<ShopSaleResponse> SellAsync(
        HttpClient owner,
        Guid venueId,
        (Guid ItemId, int Quantity) line,
        Guid? bookingId = null) =>
        await VenueScenario.ReadAsync<ShopSaleResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/shop/sales",
                new ShopSaleRequest(
                    [new ShopSaleLineRequest(line.ItemId, line.Quantity)],
                    nameof(PaymentMethod.Cash),
                    bookingId)),
            HttpStatusCode.Created);

    private static async Task<ShopItemResponse[]> BoardAsync(HttpClient owner, Guid venueId) =>
        await VenueScenario.ReadAsync<ShopItemResponse[]>(
            await owner.GetAsync($"/api/venues/{venueId}/shop/items"));

    private static async Task<SpendResponse[]> SpendingAsync(HttpClient owner, Guid venueId) =>
        await VenueScenario.ReadAsync<SpendResponse[]>(
            await owner.GetAsync($"/api/venues/{venueId}/shop/spending"));

    private static async Task<DayMoneyResponse> MoneyAsync(HttpClient owner, Guid venueId) =>
        await VenueScenario.ReadAsync<DayMoneyResponse>(
            await owner.GetAsync($"/api/venues/{venueId}/money"));

    private static async Task<DashboardResponse> DashboardAsync(HttpClient owner, Guid venueId) =>
        await VenueScenario.ReadAsync<DashboardResponse>(
            await owner.GetAsync($"/api/venues/{venueId}/dashboard"));
}
