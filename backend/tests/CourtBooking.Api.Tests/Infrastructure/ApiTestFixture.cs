using CourtBooking.Api.Email;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace CourtBooking.Api.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL container, one migrated schema and one API host for the whole test run.
/// Tests keep to their own email addresses and venue codes, so they cannot see each other's data.
/// </summary>
public sealed class ApiTestFixture : IAsyncLifetime
{
    // Keep in sync with the image used by docker-compose.yml.
    private const string Image = "postgres:17-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    private ApiFactory _root = null!;

    public FakeEmailSender Emails { get; } = new();

    public WebApplicationFactory<Program> Api { get; private set; } = null!;

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>What the API logged at Error, so a test that meets a 500 can say why.</summary>
    public CapturedLogs Errors => _root.Errors;

    /// <summary>The product events the API wrote (PRD 8), by name.</summary>
    public CapturedEvents Events => _root.Events;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        _root = new ApiFactory(ConnectionString);
        await _root.MigrateAsync();

        Api = _root.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Replace(ServiceDescriptor.Singleton<ITransactionalEmailSender>(Emails))));
    }

    public async Task DisposeAsync()
    {
        // Disposing the derived factory does not dispose the one it was derived from.
        Api.Dispose();
        var slips = _root.SlipStoragePath;
        _root.Dispose();
        await _container.DisposeAsync();

        if (Directory.Exists(slips))
        {
            Directory.Delete(slips, recursive: true);
        }
    }

    public HttpClient CreateClient() => Api.CreateClient();

    public IServiceScope CreateScope() => Api.Services.CreateScope();

    public T GetService<T>() where T : notnull => Api.Services.GetRequiredService<T>();
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiTestFixture>
{
    public const string Name = "api";
}
