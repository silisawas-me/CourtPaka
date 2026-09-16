using System.Net;
using System.Net.Http.Json;
using CourtBooking.Api.Health;
using CourtBooking.Api.Tests.Infrastructure;

namespace CourtBooking.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class HealthEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Live_returns_healthy_without_checking_dependencies()
    {
        await using var factory = new ApiFactory(UnreachableDatabase);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("Healthy", body!.Status);
        Assert.Empty(body.Checks);
    }

    [Fact]
    public async Task Ready_returns_healthy_when_database_is_reachable()
    {
        await using var factory = new ApiFactory(postgres.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("Healthy", body!.Status);
        Assert.Contains(body.Checks, check => check is { Name: "database", Status: "Healthy" });
    }

    [Fact]
    public async Task Ready_returns_service_unavailable_when_database_is_unreachable()
    {
        await using var factory = new ApiFactory(UnreachableDatabase);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("Unhealthy", body!.Status);
    }

    // Port 1 on loopback refuses connections immediately, so the check fails fast.
    private const string UnreachableDatabase =
        "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2";
}
