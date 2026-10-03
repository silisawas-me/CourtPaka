using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Tests;

/// <summary>
/// A venue's standing reads from its first step: it applied, and — if it was turned away — it
/// asked again. Both are the venue's own moves, so no admin is named on them (PRD US-10, US-20).
/// </summary>
public sealed class VenueHistoryTests(ApiTestFixture api) : IClassFixture<ApiTestFixture>
{
    private readonly VenueScenario scenario = new(api);

    [Fact]
    public async Task The_history_starts_with_the_application_and_shows_asking_again()
    {
        var admin = await scenario.PlatformAdminAsync();
        var owner = await scenario.SignedInClientAsync();
        var venue = await scenario.CreateVenueAsync(owner);

        Assert.Equal(
            HttpStatusCode.OK,
            (await admin.PostAsJsonAsync(
                $"/api/admin/venues/{venue.Id}/reject",
                new VenueDecisionRequest("เอกสารไม่ครบ"))).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PostAsync($"/api/venues/{venue.Id}/resubmit", null)).StatusCode);

        var detail = await VenueScenario.ReadAsync<AdminVenueDetailResponse>(
            await admin.GetAsync($"/api/admin/venues/{venue.Id}"));
        var moves = detail.History.OrderBy(move => move.ChangedAt).ToArray();

        Assert.Equal(
            [(null, "Pending"), ("Pending", "Rejected"), ("Rejected", "Pending")],
            moves.Select(move => (move.From, move.To)));
    }
}
