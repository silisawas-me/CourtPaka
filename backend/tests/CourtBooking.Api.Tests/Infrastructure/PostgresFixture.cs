using Testcontainers.PostgreSql;

namespace CourtBooking.Api.Tests.Infrastructure;

/// <summary>
/// Starts one real PostgreSQL container per test class (PRD 9.4). Requires Docker.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Keep in sync with the image used by docker-compose.yml.
    private const string Image = "postgres:17-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
