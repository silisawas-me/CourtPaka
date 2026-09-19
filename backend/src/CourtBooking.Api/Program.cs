using CourtBooking.Api.Data;
using CourtBooking.Api.Health;
using CourtBooking.Api.Localization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

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

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Caddy and the dev-server proxy forward "/api" unchanged, so the API mounts everything under it once here.
var api = app.MapGroup("/api");

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
