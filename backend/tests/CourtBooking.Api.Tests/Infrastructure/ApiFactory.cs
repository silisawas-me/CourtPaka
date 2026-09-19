using CourtBooking.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CourtBooking.Api.Tests.Infrastructure;

public sealed class ApiFactory(string connectionString, Action<IServiceCollection>? configureServices = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", connectionString);

        if (configureServices is not null)
        {
            builder.ConfigureServices(configureServices);
        }
    }

    /// <summary>Creates the schema from the real migrations, the same way a deployment does.</summary>
    public async Task MigrateAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }
}
