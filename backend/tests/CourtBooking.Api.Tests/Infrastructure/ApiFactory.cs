using CourtBooking.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CourtBooking.Api.Tests.Infrastructure;

public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string PrivacyPolicyVersion = "2026-09-01";

    /// <summary>What the API logged at Error, so a 500 in a test can name its cause.</summary>
    public CapturedLogs Errors { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("App:BaseUrl", "http://localhost:8080");
        builder.UseSetting("App:PrivacyPolicyVersion", PrivacyPolicyVersion);
        builder.UseSetting("App:RequireSecureCookies", "false");
        // Tests apply migrations explicitly so each suite controls when the schema appears.
        builder.UseSetting("App:ApplyMigrationsOnStartup", "false");
        // Every test shares one client IP; a production-sized limit would make unrelated tests fail.
        builder.UseSetting("App:AuthRequestsPerMinute", "10000");
        // Re-check the session against the user row on every request so revocation is testable.
        builder.UseSetting("App:SessionRevalidationSeconds", "0");

        // The suite registers a user per test; at the production hashing cost that alone would take
        // longer than everything else it does. Nothing here tests the hash itself.
        builder.ConfigureLogging(logging => logging.AddProvider(Errors));

        builder.ConfigureServices(services =>
            services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1));
    }

    /// <summary>Creates the schema from the real migrations, the same way a deployment does.</summary>
    public async Task MigrateAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }
}
