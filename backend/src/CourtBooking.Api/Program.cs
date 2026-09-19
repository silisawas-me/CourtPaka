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
        using var probeResponse = await probe.GetAsync($"http://127.0.0.1:{port}/api/health/ready");
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

// The API is only reachable through Caddy on the private Docker network (PRD 9.3),
// so forwarded headers coming from it are trusted.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
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
