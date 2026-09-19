using CourtBooking.Api.Email;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CourtBooking.Api.Tests.Infrastructure;

/// <summary>
/// One API host and one migrated schema for the whole auth test class; tests keep to their own
/// email addresses so they cannot see each other's data.
/// </summary>
public sealed class AuthApiFixture(PostgresFixture postgres) : IAsyncLifetime
{
    private ApiFactory _root = null!;

    public FakeEmailSender Emails { get; } = new();

    public WebApplicationFactory<Program> Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _root = new ApiFactory(postgres.ConnectionString);
        await _root.MigrateAsync();

        Api = _root.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Replace(ServiceDescriptor.Singleton<ITransactionalEmailSender>(Emails))));
    }

    public Task DisposeAsync()
    {
        // Disposing the derived factory does not dispose the one it was derived from.
        Api.Dispose();
        _root.Dispose();
        return Task.CompletedTask;
    }

    public HttpClient CreateClient() => Api.CreateClient();

    public IServiceScope CreateScope() => Api.Services.CreateScope();

    public T GetService<T>() where T : notnull => Api.Services.GetRequiredService<T>();
}
