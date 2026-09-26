using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Jobs;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>
/// Hours sold in advance (PRD US-31). What is tested here is that the money lands on the day it is
/// taken, that the hours are a ledger rather than a number, and that spending them settles a
/// booking without the till being told a second time.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PackageTests(ApiTestFixture api)
{
    private readonly VenueScenario scenario = new(api);

    /// <summary>Far enough ahead that the other suites are not booking into it.</summary>
    private static readonly DateOnly Day = VenueScenario.Today.AddDays(19);

    [Fact]
    public async Task An_offer_goes_on_the_board_and_says_what_an_hour_of_it_costs()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        var offer = await OfferAsync(owner, venue.Id);
        Assert.Equal(10, offer.Hours);
        Assert.Equal(1_800m, offer.PriceBaht);
        Assert.Equal(180m, offer.BahtPerHour);
        Assert.Null(offer.WithdrawnAt);

        var board = await BoardAsync(owner, venue.Id);
        Assert.Single(board, one => one.TypeId == offer.TypeId);
    }

    /// <summary>
    /// An offer is replaced, never edited — a package already sold points at the terms it was
    /// sold on, and a row that can be rewritten is not terms (BR-05).
    /// </summary>
    [Fact]
    public async Task An_offer_is_withdrawn_rather_than_changed_and_stays_readable()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var offer = await OfferAsync(owner, venue.Id);
        var package = await SellAsync(owner, venue.Id, offer.TypeId);

        var withdrawn = await VenueScenario.ReadAsync<PackageTypeResponse>(
            await owner.PostAsync($"/api/venues/{venue.Id}/packages/types/{offer.TypeId}/withdraw", null));
        Assert.NotNull(withdrawn.WithdrawnAt);

        // Nothing more is sold from it.
        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/packages",
            new SellPackageRequest(offer.TypeId, "คนอื่น", null, nameof(PaymentMethod.Cash)));
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(PackageErrorCodes.TypeUnknown, await CodeAsync(refused));

        // And it is still on the board, marked, because a sold package points at it.
        var board = await BoardAsync(owner, venue.Id);
        Assert.NotNull(Assert.Single(board, one => one.TypeId == offer.TypeId).WithdrawnAt);

        var sold = await SoldAsync(owner, venue.Id);
        Assert.Equal(offer.TypeId, Assert.Single(sold, one => one.PackageId == package.PackageId).TypeId);

        var again = await owner.PostAsync(
            $"/api/venues/{venue.Id}/packages/types/{offer.TypeId}/withdraw", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(PackageErrorCodes.TypeAlreadyWithdrawn, await CodeAsync(again));
    }

    /// <summary>
    /// Selling one is money in the till today, counted with the day's other money (PRD US-26) —
    /// and the terms are copied onto the package, so the board changing afterwards changes
    /// nothing about what was sold (BR-05).
    /// </summary>
    [Fact]
    public async Task Selling_one_is_money_in_on_the_day_it_is_sold()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var offer = await OfferAsync(owner, venue.Id);

        var before = await MoneyAsync(owner, venue.Id);
        var package = await SellAsync(owner, venue.Id, offer.TypeId);

        Assert.Equal(10, package.HoursSold);
        Assert.Equal(10, package.HoursLeft);
        Assert.Equal(180m, package.BahtPerHour);
        Assert.Equal(VenueScenario.Today.AddDays(90), package.ExpiresOn);

        // The first row of the ledger is the sale itself.
        var move = Assert.Single(package.Moves);
        Assert.Equal(10, move.Hours);
        Assert.Equal(nameof(PackageMove.Sold), move.Move);
        Assert.Null(move.BookingId);

        // And the till has it, as cash, on today.
        var after = await MoneyAsync(owner, venue.Id);
        Assert.Equal(before.CashBaht + 1_800m, after.CashBaht);
        var receipt = Assert.Single(
            after.CashReceipts, one => one.PackageId == package.PackageId);
        Assert.Null(receipt.BookingId);
        Assert.Equal(1_800m, receipt.AmountBaht);
    }

    /// <summary>
    /// Money that came in for a package is not revenue: the venue owes hours for it until they
    /// are spent, and the dashboard says how many rather than counting it as earnings (⚠️ S-27).
    /// </summary>
    [Fact]
    public async Task Hours_sold_are_owed_rather_than_earned()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var offer = await OfferAsync(owner, venue.Id);

        var before = await DashboardAsync(owner, venue.Id);
        await SellAsync(owner, venue.Id, offer.TypeId);
        var after = await DashboardAsync(owner, venue.Id);

        // Nothing in the money, everything in what is owed.
        Assert.Equal(before.OnlineBaht, after.OnlineBaht);
        Assert.Equal(before.StaffBaht, after.StaffBaht);
        Assert.Equal(before.OwedHours.Hours + 10, after.OwedHours.Hours);
        Assert.Equal(before.OwedHours.Baht + 1_800m, after.OwedHours.Baht);
        Assert.Equal(before.OwedHours.Packages + 1, after.OwedHours.Packages);
    }

    /// <summary>
    /// Hours pay for a booking the way money would, and the money is not counted twice: the till
    /// heard about it when the package was sold (PRD US-31).
    /// </summary>
    [Fact]
    public async Task Hours_pay_for_a_booking_and_the_till_is_not_told_again()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var offer = await OfferAsync(owner, venue.Id);
        var package = await SellAsync(owner, venue.Id, offer.TypeId);

        var before = await MoneyAsync(owner, venue.Id);
        var paid = await AtCounterAsync(owner, venue.Id, courts[0], 7, 2, package.PackageId);

        // The booking owes nothing and no door is offered to take more. The row's ToPayBaht is
        // the plain difference between price and receipts, as it is for anything the venue is not
        // owed for — what says whether anybody owes it is the door (CLAUDE.md, PRD US-26).
        Assert.Equal(nameof(PaymentState.Received), paid.PaymentState);
        Assert.False(paid.Can.TakeMoney);
        Assert.Equal(0m, paid.TakenBaht);
        var booking = paid;

        // Two hours came off the package, against this booking.
        var after = await OneAsync(owner, venue.Id, package.PackageId);
        Assert.Equal(8, after.HoursLeft);
        var used = Assert.Single(after.Moves, one => one.Move == nameof(PackageMove.Used));
        Assert.Equal(-2, used.Hours);
        Assert.Equal(booking.BookingId, used.BookingId);

        // And nothing new is in the till: the money arrived when the package did.
        var till = await MoneyAsync(owner, venue.Id);
        Assert.Equal(before.CashBaht, till.CashBaht);
        Assert.DoesNotContain(till.CashReceipts, one => one.BookingId == booking.BookingId);
    }

    /// <summary>
    /// What the venue has earned from that booking is what the customer paid for those hours, not
    /// the price on the board that day — which is the price they chose not to pay (⚠️ S-27).
    /// </summary>
    [Fact]
    public async Task What_is_earned_is_what_the_hours_cost_not_what_the_board_says()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var offer = await OfferAsync(owner, venue.Id);
        var package = await SellAsync(owner, venue.Id, offer.TypeId);

        var before = await DashboardAsync(owner, venue.Id);

        // Two hours at the board's 200 an hour, paid for with hours that cost 180.
        var booking = await AtCounterAsync(owner, venue.Id, courts[0], 7, 2, package.PackageId);
        Assert.Equal(400m, booking.TotalBaht);

        await scenario.PlayOutAsync(booking.BookingId);
        var after = await DashboardAsync(owner, venue.Id);

        Assert.Equal(before.StaffBaht + 360m, after.StaffBaht);
        Assert.Equal(before.OwedHours.Hours - 2, after.OwedHours.Hours);
    }

    [Fact]
    public async Task A_package_pays_for_a_booking_whole_or_not_at_all()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var small = await VenueScenario.ReadAsync<PackageTypeResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/packages/types",
                new PackageTypeRequest("ชุดเล็ก", 1, 200m, 90)),
            HttpStatusCode.Created);

        var package = await SellAsync(owner, venue.Id, small.TypeId);

        var refused = await CounterAsync(
            owner, venue.Id, courts[0], 9, 3, package.PackageId);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(PackageErrorCodes.NotEnoughHours, await CodeAsync(refused));

        // And nothing came off it, and nothing was put aside.
        Assert.Equal(1, (await OneAsync(owner, venue.Id, package.PackageId)).HoursLeft);
    }

    /// <summary>Hours pay instead of money, not alongside it.</summary>
    [Fact]
    public async Task A_booking_somebody_has_already_paid_money_against_is_not_paid_with_hours()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var offer = await OfferAsync(owner, venue.Id);
        var package = await SellAsync(owner, venue.Id, offer.TypeId);

        // Sold and paid for with money at the counter, which is the end of the question.
        var booking = await AtCounterAsync(owner, venue.Id, courts[0], 11, 1);

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{booking.BookingId}/pay-with-package",
            new SpendPackageRequest(package.PackageId));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(PackageErrorCodes.AlreadyPaidFor, await CodeAsync(refused));
    }

    /// <summary>
    /// A booking hours paid for gives back hours when it is let go, not money (PRD US-31, BR-06).
    /// The money is not the venue's to send anywhere: it came in weeks ago for something else.
    /// </summary>
    [Fact]
    public async Task Letting_one_go_gives_back_hours_and_no_money()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var offer = await OfferAsync(owner, venue.Id);
        var package = await SellAsync(owner, venue.Id, offer.TypeId);
        var booking = await AtCounterAsync(owner, venue.Id, courts[0], 13, 2, package.PackageId);

        var cancelled = await VenueScenario.ReadAsync<VenueBookingResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings/{booking.BookingId}/cancel",
                new VenueCancelRequest(nameof(CancellationReason.VenueInitiated), null, "ปิดคอร์ท")));

        // Nothing owed back in money.
        Assert.Equal(0m, cancelled.RefundDueBaht);
        Assert.Equal(0m, cancelled.OutstandingBaht);

        // The hours are back, under the booking's own terms — all of them, this far ahead.
        var after = await OneAsync(owner, venue.Id, package.PackageId);
        Assert.Equal(10, after.HoursLeft);
        var back = Assert.Single(after.Moves, one => one.Move == nameof(PackageMove.GivenBack));
        Assert.Equal(2, back.Hours);
        Assert.Equal(booking.BookingId, back.BookingId);
    }

    /// <summary>
    /// Hours that ran out stop being hours the venue owes, and the ledger says so rather than the
    /// balance simply falling to nothing (⚠️ S-28).
    /// </summary>
    [Fact]
    public async Task Hours_that_ran_out_are_written_off_and_the_ledger_says_when()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var offer = await OfferAsync(owner, venue.Id);
        var package = await SellAsync(owner, venue.Id, offer.TypeId);

        await RanOutAsync(package.PackageId);
        await ExpireAsync();

        var after = await OneAsync(owner, venue.Id, package.PackageId);
        Assert.Equal(0, after.HoursLeft);
        Assert.NotNull(after.ExpiredAt);

        var written = Assert.Single(after.Moves, one => one.Move == nameof(PackageMove.Expired));
        Assert.Equal(-10, written.Hours);

        // It stops being owed, and a second sweep writes nothing more.
        Assert.DoesNotContain(
            (await DashboardAsync(owner, venue.Id)).OwedHours.Hours,
            new[] { -1 });
        await ExpireAsync();
        Assert.Equal(2, (await OneAsync(owner, venue.Id, package.PackageId)).Moves.Length);
    }

    [Fact]
    public async Task Hours_that_ran_out_cannot_be_spent()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var offer = await OfferAsync(owner, venue.Id);
        var package = await SellAsync(owner, venue.Id, offer.TypeId);

        await RanOutAsync(package.PackageId);

        var refused = await CounterAsync(owner, venue.Id, courts[0], 15, 1, package.PackageId);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(PackageErrorCodes.RunOut, await CodeAsync(refused));
    }

    [Theory]
    [InlineData("", 10, 1_800, 90, PackageErrorCodes.InvalidName)]
    [InlineData("ชุด", 0, 1_800, 90, PackageErrorCodes.InvalidHours)]
    [InlineData("ชุด", 10, 0, 90, PackageErrorCodes.InvalidPrice)]
    [InlineData("ชุด", 10, 1_800, 0, PackageErrorCodes.InvalidValidity)]
    public async Task An_offer_that_is_not_an_offer_is_refused(
        string name,
        int hours,
        decimal price,
        int days,
        string code)
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();

        var refused = await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/packages/types",
            new PackageTypeRequest(name, hours, price, days));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(code, await CodeAsync(refused));
    }

    /// <summary>
    /// A package names the customer who bought it, so every door of it — the reading included —
    /// is behind the permission that names belong behind (PDPA, as PRD US-13).
    /// </summary>
    [Fact]
    public async Task Somebody_without_the_permission_cannot_read_what_was_sold()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var staff = await scenario.StaffClientAsync(
            owner, venue.Id, nameof(VenuePermissions.VerifySlip));

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await staff.GetAsync($"/api/venues/{venue.Id}/packages")).StatusCode);
    }

    /// <summary>Selling is selling, and a suspension stops it (PRD US-20).</summary>
    [Fact]
    public async Task A_suspended_venue_cannot_sell_one_but_can_still_read_them()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var offer = await OfferAsync(owner, venue.Id);
        await SellAsync(owner, venue.Id, offer.TypeId);

        await scenario.SetStatusAsync(venue.Id, VenueStatus.Suspended);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/packages",
                new SellPackageRequest(
                    offer.TypeId, "คนซื้อ", null, nameof(PaymentMethod.Cash)))).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.GetAsync($"/api/venues/{venue.Id}/packages")).StatusCode);
    }

    /// <summary>A package is one venue's. Another venue's booking cannot be paid with it.</summary>
    [Fact]
    public async Task A_package_cannot_be_spent_at_another_venue()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var offer = await OfferAsync(owner, venue.Id);
        var package = await SellAsync(owner, venue.Id, offer.TypeId);

        var (elsewhere, other, courts) = await scenario.BookableVenueAsync();

        var refused = await CounterAsync(
            elsewhere, other.Id, courts[0], 17, 1, package.PackageId);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(PackageErrorCodes.NotFound, await CodeAsync(refused));
    }

    /// <summary>
    /// The other door: a booking the venue is already holding and nobody has paid for — a week of
    /// a standing arrangement (PRD US-30) — settled with hours afterwards.
    /// </summary>
    [Fact]
    public async Task Hours_settle_a_booking_that_was_standing_unpaid()
    {
        var (owner, venue, courts) = await scenario.BookableVenueAsync();
        var offer = await OfferAsync(owner, venue.Id);
        var package = await SellAsync(owner, venue.Id, offer.TypeId);

        var week = Day.AddDays(1);
        var agreed = await VenueScenario.ReadAsync<BookingSeriesResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/series",
                new BookingSeriesRequest(
                    courts[0],
                    week.DayOfWeek.ToString(),
                    19,
                    21,
                    "ก๊วนที่ซื้อชั่วโมงไว้",
                    null,
                    week,
                    week)),
            HttpStatusCode.Created);

        using (var scope = api.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SeriesBookings>()
                .WorkAsync(CancellationToken.None);
        }

        var made = await VenueScenario.ReadAsync<VenueBookingResponse[]>(
            await owner.GetAsync(
                $"/api/venues/{venue.Id}/bookings?date={week:yyyy-MM-dd}"));

        var booking = Assert.Single(
            made, one => one.CustomerName == "ก๊วนที่ซื้อชั่วโมงไว้" && one.Can.TakeMoney);

        await SpendAsync(owner, venue.Id, booking.BookingId, package.PackageId);

        Assert.Equal(8, (await OneAsync(owner, venue.Id, package.PackageId)).HoursLeft);
        Assert.Equal("Ended", (await VenueScenario.ReadAsync<BookingSeriesStoppedResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/series/{agreed.SeriesId}/stop",
                new BookingSeriesStopRequest(null)))).Series.State);
    }

    /// <summary>
    /// Packages are kept for ever, so the list has to stop somewhere. What it never stops showing
    /// is one that still has hours on it (PRD US-31).
    /// </summary>
    [Fact]
    public async Task The_list_keeps_everything_live_and_only_the_last_few_that_are_finished()
    {
        var (owner, venue, _) = await scenario.BookableVenueAsync();
        var offer = await OfferAsync(owner, venue.Id);

        for (var number = 0; number < Packages.FinishedShown + 2; number++)
        {
            var spent = await SellAsync(owner, venue.Id, offer.TypeId);
            await RanOutAsync(spent.PackageId);
            await ExpireAsync();
        }

        var live = await SellAsync(owner, venue.Id, offer.TypeId);

        var all = await SoldAsync(owner, venue.Id);
        Assert.Equal(Packages.FinishedShown, all.Count(one => one.ExpiredAt is not null));
        Assert.Single(all, one => one.PackageId == live.PackageId);
    }

    private async Task<PackageTypeResponse> OfferAsync(HttpClient owner, Guid venueId) =>
        await VenueScenario.ReadAsync<PackageTypeResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/packages/types",
                new PackageTypeRequest("ชุด 10 ชั่วโมง", 10, 1_800m, 90)),
            HttpStatusCode.Created);

    private static async Task<HourPackageResponse> SellAsync(
        HttpClient owner,
        Guid venueId,
        Guid typeId) =>
        await VenueScenario.ReadAsync<HourPackageResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/packages",
                new SellPackageRequest(typeId, "ก๊วนเหมา", "0812345678", nameof(PaymentMethod.Cash))),
            HttpStatusCode.Created);

    private static async Task SpendAsync(
        HttpClient owner,
        Guid venueId,
        Guid bookingId,
        Guid packageId) =>
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PostAsJsonAsync(
                $"/api/venues/{venueId}/bookings/{bookingId}/pay-with-package",
                new SpendPackageRequest(packageId))).StatusCode);

    private static async Task<VenueBookingResponse> AtCounterAsync(
        HttpClient owner,
        Guid venueId,
        Guid courtId,
        int fromHour,
        int hours,
        Guid? packageId = null) =>
        await VenueScenario.ReadAsync<VenueBookingResponse>(
            await CounterAsync(owner, venueId, courtId, fromHour, hours, packageId),
            HttpStatusCode.Created);

    private static Task<HttpResponseMessage> CounterAsync(
        HttpClient owner,
        Guid venueId,
        Guid courtId,
        int fromHour,
        int hours,
        Guid? packageId = null) =>
        owner.PostAsJsonAsync(
            $"/api/venues/{venueId}/bookings",
            new CounterBookingRequest(
                [.. Enumerable.Range(fromHour, hours)
                    .Select(hour => new BookingSlotRequest(courtId, Day, hour))],
                "ลูกค้าเหมา",
                null,
                packageId is null ? nameof(CounterPayment.Cash) : null,
                packageId));

    private static async Task<PackageTypeResponse[]> BoardAsync(HttpClient owner, Guid venueId) =>
        await VenueScenario.ReadAsync<PackageTypeResponse[]>(
            await owner.GetAsync($"/api/venues/{venueId}/packages/types"));

    private static async Task<HourPackageResponse[]> SoldAsync(HttpClient owner, Guid venueId) =>
        await VenueScenario.ReadAsync<HourPackageResponse[]>(
            await owner.GetAsync($"/api/venues/{venueId}/packages"));

    private static async Task<HourPackageResponse> OneAsync(
        HttpClient owner,
        Guid venueId,
        Guid packageId) =>
        Assert.Single(await SoldAsync(owner, venueId), one => one.PackageId == packageId);

    private static async Task<DayMoneyResponse> MoneyAsync(HttpClient owner, Guid venueId) =>
        await VenueScenario.ReadAsync<DayMoneyResponse>(
            await owner.GetAsync($"/api/venues/{venueId}/money"));

    private static async Task<DashboardResponse> DashboardAsync(HttpClient owner, Guid venueId) =>
        await VenueScenario.ReadAsync<DashboardResponse>(
            await owner.GetAsync($"/api/venues/{venueId}/dashboard"));

    /// <summary>Moves a package's last day into the past, which is the only thing time does here.</summary>
    private async Task RanOutAsync(Guid packageId)
    {
        using var scope = api.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await database.HourPackages
            .Where(one => one.Id == packageId)
            .ExecuteUpdateAsync(set => set.SetProperty(
                one => one.ExpiresOn, VenueScenario.Today.AddDays(-1)));
    }

    private async Task ExpireAsync()
    {
        using var scope = api.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PackageExpiry>()
            .WorkAsync(CancellationToken.None);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemBody>())?.Code;

    private sealed record ProblemBody(string? Code);
}
