using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Tests.Infrastructure;

namespace CourtBooking.Api.Tests;

/// <summary>
/// PRD 8 Observability: an event every time a booking's status changes (booking_{status} for every
/// status in 6.1) and the named ones beside them — checked by walking the flows that write them,
/// which is what "ตรวจว่ามี event ครบใน E2E test" asks for.
/// </summary>
public sealed class EventCoverageTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    /// <summary>The names PRD 8 lists, less the commission ones, which arrive with US-21.</summary>
    private static readonly string[] Required =
    [
        "booking_held",
        "booking_pending_verification",
        "booking_confirmed",
        "booking_completed",
        "booking_cancelled",
        "booking_expired",
        "booking_rejected",
        "booking_no_show",
        "venue_page_viewed",
        "slip_uploaded",
        "slip_replaced",
        "refund_recorded",
        "refund_voided",
        "venue_applied",
        "venue_approved",
        "venue_suspended",
        "complaint_opened",
    ];

    [Fact]
    public async Task Every_event_PRD_8_names_is_written_by_the_flow_it_belongs_to()
    {
        var admin = await scenario.PlatformAdminAsync();
        var tomorrow = VenueScenario.Today.AddDays(1);

        // A venue applies, is approved, and is later suspended (venue_*).
        var applicant = await scenario.SignedInClientAsync();
        var applied = await scenario.CreateVenueAsync(applicant);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/venues/{applied.Id}/approve", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PostAsJsonAsync(
                $"/api/admin/venues/{applied.Id}/suspend",
                new Venues.VenueDecisionRequest("ทดสอบ"))).StatusCode);

        var (owner, venue, courts) = await scenario.BookableVenueAsync(courts: 2);

        // Looking (venue_page_viewed).
        await api.CreateClient().GetAsync($"/api/venues/{venue.Id}/availability?date={tomorrow:yyyy-MM-dd}");

        // A slip sent, then sent again (booking_held, slip_uploaded, booking_pending_verification,
        // slip_replaced), then confirmed after the hours were played (booking_confirmed, and the
        // clock's booking_completed in the same write).
        var (booker, paid) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 18);
        await VenueScenario.UploadAsync(booker, paid.Id, VenueScenario.Jpeg());
        await scenario.PlayOutAsync(paid.Id);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{paid.Id}/confirm", null);

        // Turned down with the money received, then refunded and the refund taken back
        // (booking_rejected, refund_recorded, refund_voided).
        var (_, turned) = await scenario.WaitingBookingAsync(venue.Id, courts[0], 19);
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/slip-queue/{turned.Id}/reject",
            new RejectSlipRequest("ยอดไม่ตรง", PaymentReceived: true));
        var refunds = await VenueScenario.ReadAsync<RefundsResponse>(
            await owner.PostAsJsonAsync(
                $"/api/venues/{venue.Id}/bookings/{turned.Id}/refunds",
                new RecordRefundRequest(100m, VenueScenario.Today, nameof(RefundMethod.Transfer), null)));
        await owner.PostAsJsonAsync(
            $"/api/venues/{venue.Id}/bookings/{turned.Id}/refunds/{refunds.Records[0].Id}/void",
            new VoidRefundRequest("โอนผิดบัญชี"));

        // Confirmed and nobody came (booking_no_show).
        var (_, missed) = await scenario.WaitingBookingAsync(venue.Id, courts[1], 18);
        await owner.PostAsync($"/api/venues/{venue.Id}/slip-queue/{missed.Id}/confirm", null);
        await scenario.StartsInAsync(missed.Id, TimeSpan.FromMinutes(-20));
        await owner.PostAsync($"/api/venues/{venue.Id}/bookings/{missed.Id}/no-show", null);

        // Given up by the booker (booking_cancelled).
        var quitter = await scenario.SignedInClientAsync();
        var given = await VenueScenario.HoldAsync(quitter, venue.Id, tomorrow, (courts[1], 20));
        await quitter.PostAsync($"/api/bookings/{given.Id}/cancel", null);

        // A hold left to run out, found by its booker's next hold (booking_expired).
        var slow = await scenario.SignedInClientAsync();
        var lapsed = await VenueScenario.HoldAsync(slow, venue.Id, tomorrow, (courts[1], 21));
        await scenario.LapseHoldAsync(lapsed.Id);
        await VenueScenario.HoldAsync(slow, venue.Id, tomorrow, (courts[0], 21));

        // A complaint about one of them (complaint_opened).
        await admin.PostAsJsonAsync(
            "/api/admin/complaints", new OpenComplaintRequest(paid.Id.ToString(), "ทดสอบ", "Email"));

        var written = api.Events.Names.ToHashSet();
        Assert.All(Required, name => Assert.Contains(name, written));
    }
}
