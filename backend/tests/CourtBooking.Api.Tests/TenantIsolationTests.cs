using System.Net;
using CourtBooking.Api.Tests.Infrastructure;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests;

/// <summary>
/// PRD 8, first row: somebody from venue A cannot read or change venue B, at every endpoint.
///
/// The per-feature tests each check their own door. This checks that there is no door without a
/// lock: it walks the routes the application actually mapped, so an endpoint added under a venue
/// without saying what it needs fails here rather than in front of a venue.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TenantIsolationTests(ApiTestFixture api)
{
    private readonly VenueScenario scenario = new(api);

    /// <summary>
    /// The one thing under a venue that anybody may read: the grid a booker is looking at before
    /// they have an account, which says nothing about who booked the hours (PRD US-02).
    /// Anything else added here has to be a deliberate decision, which is the point of the list.
    /// It is matched exactly, so a route that changes shape stops being excused and fails.
    /// </summary>
    private static readonly string[] PublicOnPurpose = ["/api/venues/{venueId:guid}/availability"];

    /// <summary>
    /// Every route that names one venue: anything under /api/venues/ whose next segment is a
    /// parameter, whatever that parameter is called. Matching the name would let a route that
    /// spelled it differently slip past the very check it needs — and the group it hangs from
    /// asks only for a signed-in user, so it would be open to every account on the platform.
    ///
    /// The platform's own doors (/api/admin/...) are somebody else's rule: they are for people
    /// who are deliberately not members (PRD US-20), and PlatformAdminTests checks them.
    /// </summary>
    private static IReadOnlyList<RouteEndpoint> VenueRoutes(ApiTestFixture api) =>
    [
        .. api.Api.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint =>
                endpoint.RoutePattern.RawText is { } route
                && route.StartsWith("/api/venues/{", StringComparison.Ordinal)
                && !PublicOnPurpose.Contains(route))
    ];

    [Fact]
    public void Every_endpoint_under_a_venue_says_what_it_needs()
    {
        var routes = VenueRoutes(api);
        Assert.NotEmpty(routes);

        var unguarded = routes
            .Where(endpoint =>
                endpoint.Metadata.GetMetadata<AuthorizationPolicy>() is not { } policy
                || !policy.Requirements.OfType<VenuePermissionRequirement>().Any())
            .Select(endpoint => $"{Methods(endpoint)} {endpoint.RoutePattern.RawText}")
            .ToArray();

        Assert.True(
            unguarded.Length == 0,
            "These routes carry a venue's data but do not say what a caller needs "
            + $"(Member, OwnerOnly or Needs(...) — see VenueEndpoints): {string.Join(", ", unguarded)}");

        // And the venue they name has to be the one the handler reads, which it finds by the
        // route value "venueId" (VenuePermissionHandler). A route that called it something else
        // would carry a requirement that could never be answered.
        var misnamed = routes
            .Where(endpoint => !endpoint.RoutePattern.Parameters.Any(parameter => parameter.Name == "venueId"))
            .Select(endpoint => $"{Methods(endpoint)} {endpoint.RoutePattern.RawText}")
            .ToArray();

        Assert.True(
            misnamed.Length == 0,
            $"These routes name a venue by another name than venueId: {string.Join(", ", misnamed)}");
    }

    /// <summary>
    /// And the lock is not decorative: every one of those reads, driven for real, refuses a
    /// signed-in stranger who owns another venue. Reads are the ones that can be walked without
    /// inventing a body; the writes are covered by the rule above and by their own tests.
    /// </summary>
    [Fact]
    public async Task A_stranger_reads_nothing_of_somebody_elses_venue()
    {
        var (owner, mine, _) = await scenario.BookableVenueAsync();
        var stranger = await scenario.SignedInClientAsync();
        await scenario.CreateVenueAsync(stranger);

        var reads = VenueRoutes(api)
            .Where(endpoint =>
                Methods(endpoint).Contains("GET", StringComparison.Ordinal)
                && endpoint.RoutePattern.Parameters.All(parameter => parameter.Name == "venueId"))
            .Select(endpoint => endpoint.RoutePattern.RawText!.Replace("{venueId:guid}", mine.Id.ToString()))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(reads);

        foreach (var url in reads)
        {
            // The owner first, so a URL that has stopped routing fails here instead of passing as
            // a refusal: 404 is a fine answer to a stranger and a useless one to a test. What the
            // owner gets may still be a 400 — several of these want a date — but never a closed
            // door, which is what this is establishing.
            var toTheOwner = await owner.GetAsync(url);
            Assert.True(
                toTheOwner.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Forbidden),
                $"GET {url} answered {(int)toTheOwner.StatusCode} to the venue's own owner");

            var answer = await stranger.GetAsync(url);
            Assert.True(
                answer.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
                $"GET {url} answered {(int)answer.StatusCode} to somebody who is not a member");
        }
    }

    private static string Methods(Endpoint endpoint) =>
        string.Join('/', endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["?"]);
}
