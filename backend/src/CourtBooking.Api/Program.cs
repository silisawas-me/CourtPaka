using CourtBooking.Api;
using CourtBooking.Api.Data;
using CourtBooking.Api.Email;
using CourtBooking.Api.Health;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

const string ReadyTag = "ready";

// The runtime image has no shell or curl, so the container healthcheck re-invokes this binary
// with --healthcheck and it probes its own readiness endpoint (docker-compose.yml).
if (args.Contains("--healthcheck"))
{
    var port = (Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080").Split(';')[0];
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        using var probeResponse = await probe.GetAsync($"http://127.0.0.1:{port}/api/health/live");
        return probeResponse.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        return 1;
    }
}

PlatformRequirements.EnsureAvailable();

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>(name: "database", tags: [ReadyTag]);
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITransactionalEmailSender, LoggingEmailSender>();

// Missing or malformed values fail the deployment at startup, not at first use.
builder.Services.AddOptions<AppOptions>()
    .BindConfiguration(AppOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
var appOptions = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>();

builder.Services
    .AddIdentityCore<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = false; // Verification gates booking, not signing in (PRD US-01).
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        // Five failures lock the account for fifteen minutes (PRD US-01).
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

// Re-checks each sign-in cookie against the user row, so lockouts, deletions and password changes
// take effect on live sessions instead of waiting out the 14-day cookie.
builder.Services.TryAddScoped<ISecurityStampValidator, SecurityStampValidator<AppUser>>();
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = TimeSpan.FromSeconds(appOptions?.SessionRevalidationSeconds ?? 900));

// Without a persisted key ring the keys that encrypt auth cookies are regenerated on every restart,
// which signs every user out and breaks a second replica outright.
if (!string.IsNullOrWhiteSpace(appOptions?.DataProtectionKeysPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(Directory.CreateDirectory(appOptions.DataProtectionKeysPath))
        .SetApplicationName("CourtPaka");
}

// AddIdentityCookies registers every scheme Identity signs out of (application, external, two-factor),
// which the session validator needs when it rejects a cookie.
builder.Services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();

builder.Services.ConfigureApplicationCookie(options =>
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Every deployed environment sits behind Caddy's TLS; only dev machines and tests turn this off.
        options.Cookie.SecurePolicy = appOptions?.RequireSecureCookies ?? true
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        // An API answers with status codes; redirects to login pages belong to the SPA.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync;
    });

builder.Services.AddAuthorization();

// Registration and password endpoints send email and check credentials, so they are capped per client IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicies.Auth, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = appOptions?.AuthRequestsPerMinute ?? 10,
                Window = TimeSpan.FromMinutes(1),
            }));
});

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddOpenApi();
}

// Only Caddy on the private Docker network may set X-Forwarded-*; anything else is ignored so the
// client IP used by audit logs and rate limiting cannot be spoofed (PRD 8, 9.3).
// Override with ForwardedHeaders:KnownNetworks (comma-separated CIDRs) when the proxy sits elsewhere.
var knownProxyNetworks = (builder.Configuration["ForwardedHeaders:KnownNetworks"] ?? "127.0.0.1/8,::1/128,172.16.0.0/12")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(System.Net.IPNetwork.Parse)
    .ToArray();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var network in knownProxyNetworks)
    {
        options.KnownIPNetworks.Add(network);
    }
});

var app = builder.Build();

// Local stacks bring their own schema up; deployments run the migration bundle before the swap (PRD 9.4).
if (app.Services.GetRequiredService<IOptions<AppOptions>>().Value.ApplyMigrationsOnStartup)
{
    using var migrationScope = app.Services.CreateScope();
    await migrationScope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Caddy and the dev-server proxy forward "/api" unchanged, so the API mounts everything under it once here.
var api = app.MapGroup("/api");

api.MapAuthEndpoints();

// Liveness: the process is running. Readiness: dependencies such as the database are reachable.
api.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthResponseWriter.WriteAsync,
});
api.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(ReadyTag),
    ResponseWriter = HealthResponseWriter.WriteAsync,
});

app.Run();

return 0;
