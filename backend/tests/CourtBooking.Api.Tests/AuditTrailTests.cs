using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CourtBooking.Api.Tests;

/// <summary>
/// The history tables cannot be rewritten, by the application or by anybody else with the
/// database (PRD 8, US-18, US-22).
/// </summary>
public sealed class AuditTrailTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Theory]
    [InlineData("BookingStatusChanges")]
    [InlineData("VenueStatusChanges")]
    [InlineData("AccountStatusChanges")]
    [InlineData("CourtStatusChanges")]
    [InlineData("UserConsents")]
    [InlineData("SlipViewings")]
    [InlineData("MembershipChanges")]
    [InlineData("BookingArrivalChanges")]
    [InlineData("BookingSlotChanges")]
    [InlineData("PaymentReceipts")]
    [InlineData("DailyClosings")]
    [InlineData("SeriesMisses")]
    [InlineData("PackageEntries")]
    [InlineData("StockEntries")]
    public async Task A_history_row_cannot_be_changed_or_removed(string table)
    {
        await EveryHistoryHasARowAsync();
        Assert.True(await HasRowsAsync(table), $"{table} has no row to test against");

        var changed = await RefusedAsync(
            $"""UPDATE "{table}" SET "Id" = "Id" WHERE "Id" = (SELECT "Id" FROM "{table}" LIMIT 1)""");
        var removed = await RefusedAsync(
            $"""DELETE FROM "{table}" WHERE "Id" = (SELECT "Id" FROM "{table}" LIMIT 1)""");
        var emptied = await RefusedAsync($"""TRUNCATE "{table}" CASCADE""");

        Assert.True(changed, $"{table} accepted an UPDATE");
        Assert.True(removed, $"{table} accepted a DELETE");
        Assert.True(emptied, $"{table} accepted a TRUNCATE");
    }

    /// <summary>The one change the product makes to a refund record: voiding it, once (US-18).</summary>
    [Fact]
    public async Task A_refund_record_can_be_voided_once_and_nothing_else()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var (_, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/slip-queue/{booking.Id}/reject",
            new RejectSlipRequest("ยอดไม่ตรง", PaymentReceived: true));
        var written = await VenueScenario.ReadAsync<RefundsResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds",
                new RecordRefundRequest(100m, VenueScenario.Today, nameof(RefundMethod.Transfer), null)));
        var record = Assert.Single(written.Records).Id;

        // The amount of a record is not something anybody gets to change afterwards.
        Assert.True(await RefusedAsync(
            $"""UPDATE "RefundRecords" SET "AmountBaht" = 1 WHERE "Id" = '{record}'"""));
        Assert.True(await RefusedAsync($"""DELETE FROM "RefundRecords" WHERE "Id" = '{record}'"""));

        var voided = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.Id}/refunds/{record}/void",
            new VoidRefundRequest("โอนผิดบัญชี"));
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        // Voided, it stays as it was voided: not un-voided, not voided again with another reason.
        Assert.True(await RefusedAsync(
            $"""UPDATE "RefundRecords" SET "VoidedAt" = NULL WHERE "Id" = '{record}'"""));
        Assert.True(await RefusedAsync(
            $"""UPDATE "RefundRecords" SET "VoidReason" = 'อย่างอื่น' WHERE "Id" = '{record}'"""));
    }

    /// <summary>
    /// What a venue paid out is a record like a refund is: the only thing that may happen to it
    /// afterwards is being voided, once, with a reason. Its own trigger, so its own test — the
    /// theory above covers the tables that refuse every write, and this one refuses all but three
    /// columns (PRD US-33).
    /// </summary>
    [Fact]
    public async Task What_a_venue_paid_out_can_only_ever_be_voided()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        var spend = await VenueScenario.ReadAsync<SpendResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/spending",
                new SpendRequest(
                    nameof(SpendKind.Utilities), 480m, null, nameof(PaymentMethod.Cash),
                    "ค่าน้ำ", null, null)),
            HttpStatusCode.Created);

        // What was paid, for what, on what day, by whom: none of it moves.
        Assert.True(await RefusedAsync(
            $"""UPDATE "Spends" SET "AmountBaht" = 1 WHERE "Id" = '{spend.SpendId}'"""));
        Assert.True(await RefusedAsync(
            $"""UPDATE "Spends" SET "Kind" = 5 WHERE "Id" = '{spend.SpendId}'"""));
        Assert.True(await RefusedAsync(
            $"""UPDATE "Spends" SET "Note" = 'อย่างอื่น' WHERE "Id" = '{spend.SpendId}'"""));
        Assert.True(await RefusedAsync(
            $"""DELETE FROM "Spends" WHERE "Id" = '{spend.SpendId}'"""));
        Assert.True(await RefusedAsync("""TRUNCATE "Spends" CASCADE"""));

        var voided = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/spending/{spend.SpendId}/void",
            new ShopSaleCancelRequest("คีย์ผิด"));
        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        // Voided, it stays as it was voided: not un-voided, not voided again with another reason.
        Assert.True(await RefusedAsync(
            $"""UPDATE "Spends" SET "VoidedAt" = NULL WHERE "Id" = '{spend.SpendId}'"""));
        Assert.True(await RefusedAsync(
            $"""UPDATE "Spends" SET "VoidReason" = 'อย่างอื่น' WHERE "Id" = '{spend.SpendId}'"""));
    }

    /// <summary>
    /// Puts at least one row in every history table the way the product does: a booking with a
    /// slip, a venue the platform decided on, a suspended account, and a complaint whose slip was
    /// looked at. A row trigger never fires on an empty table, so without this a missing trigger
    /// would pass for a present one.
    /// </summary>
    private async Task EveryHistoryHasARowAsync()
    {
        var admin = await scenario.PlatformAdminAsync();
        var (venueOwner, venue, courts) = await scenario.BookableVenueAsync();
        var (booker, booking) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);

        var owner = await scenario.SignedInClientAsync();
        var pending = await scenario.CreateVenueAsync(owner);
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PostAsync($"/api/admin/venues/{pending.Id}/approve", null)).StatusCode);

        var me = await VenueScenario.ReadAsync<Identity.CurrentUserResponse>(
            await booker.GetAsync("/api/auth/me"));
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PostAsJsonAsync(
                $"/api/admin/users/{me.Id}/suspend",
                new Identity.AccountStandingRequest("ทดสอบ"))).StatusCode);

        // An hour added to a booking, which is the row BookingSlotChanges keeps (PRD US-29).
        // Before the hours are moved about below: the hour it runs on into has to be one the
        // venue is open for.
        Assert.Equal(
            HttpStatusCode.OK,
            (await venueOwner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings/{booking.Id}/extend",
                new ExtendBookingRequest(null))).StatusCode);

        // Somebody turned up, which is the row BookingArrivalChanges keeps (PRD US-24).
        await scenario.StartsInAsync(booking.Id, TimeSpan.FromMinutes(5));
        Assert.Equal(
            HttpStatusCode.OK,
            (await venueOwner.PostAsync(
                $"/api/venues/{venue.Id}/bookings/{booking.Id}/confirm-arrival", null)).StatusCode);

        // Money that came in, and a till that was counted (PRD US-26).
        Assert.Equal(
            HttpStatusCode.OK,
            (await venueOwner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/money/closing",
                new CloseDayRequest(1_000m, 1_000m, null))).StatusCode);

        var complaint = await VenueScenario.ReadAsync<ComplaintResponse>(
            await admin.PostAsJsonAsync(
                "/api/admin/complaints",
                new OpenComplaintRequest(booking.Id.ToString(), "ทดสอบ", "Email")),
            HttpStatusCode.Created);
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.GetAsync($"/api/admin/complaints/{complaint.Id}/slip")).StatusCode);

        // Something on the shelf, which is the row StockEntries keeps (PRD US-32, US-33).
        var stock = await VenueScenario.ReadAsync<ShopItemResponse>(
            await venueOwner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/shop/items",
                new ShopItemRequest("ลูกขนไก่ทดสอบ", 90m, "ลูก", true, null)),
            HttpStatusCode.Created);

        Assert.Equal(
            HttpStatusCode.Created,
            (await venueOwner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/spending",
                new SpendRequest(
                    nameof(SpendKind.Stock), 700m, null, nameof(PaymentMethod.PromptPay),
                    null, stock.ItemId, 10))).StatusCode);

        // Hours somebody bought, which is the row PackageEntries keeps (PRD US-31).
        var offer = await VenueScenario.ReadAsync<PackageTypeResponse>(
            await venueOwner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/packages/types",
                new PackageTypeRequest("ชุดทดสอบ", 10, 1_800m, 90)),
            HttpStatusCode.Created);

        Assert.Equal(
            HttpStatusCode.Created,
            (await venueOwner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/packages",
                new SellPackageRequest(
                    offer.TypeId, "คนซื้อ", null, nameof(PaymentMethod.Cash)))).StatusCode);

        // A week a standing arrangement could not have, which is the row SeriesMisses keeps
        // (PRD US-30). Agreed on an hour the venue is shut for, so the sweep has to report it.
        var week = VenueScenario.Today.AddDays(9);
        Assert.Equal(
            HttpStatusCode.Created,
            (await venueOwner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/series",
                new BookingSeriesRequest(
                    courts[0], week.DayOfWeek.ToString(), 23, 24, "ก๊วนทดสอบ", null, week, week)))
                .StatusCode);

        using (var scope = api.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Jobs.SeriesBookings>()
                .WorkAsync(CancellationToken.None);
        }
    }

    private async Task<bool> HasRowsAsync(string table)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // The table name is one of this class's own constants, never input.
        var sql = "SELECT count(*)::int AS \"Value\" FROM \"" + table + "\"";
        var found = await database.Database.SqlQueryRaw<int>(sql).ToListAsync();
        return found.Single() > 0;
    }

    /// <summary>
    /// Runs a statement that the database should refuse, and answers whether it did — with the
    /// trigger's own error, not some other failure that would make the test pass for nothing.
    /// </summary>
    private async Task<bool> RefusedAsync(string sql)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            await database.Database.ExecuteSqlRawAsync(sql);
            return false;
        }
        catch (PostgresException refused) when (refused.SqlState == PostgresErrorCodes.RaiseException)
        {
            return true;
        }
    }
}
