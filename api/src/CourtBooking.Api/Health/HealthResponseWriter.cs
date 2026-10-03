using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CourtBooking.Api.Health;

/// <summary>
/// Writes a compact JSON health report. Exception details are deliberately omitted
/// because the endpoints are public (used by the uptime monitor, PRD 9.3).
/// </summary>
public static class HealthResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var body = new HealthResponse(
            report.Status.ToString(),
            report.Entries.Select(entry => new HealthEntry(entry.Key, entry.Value.Status.ToString())).ToArray());

        return context.Response.WriteAsJsonAsync(body);
    }
}

public sealed record HealthResponse(string Status, HealthEntry[] Checks);

public sealed record HealthEntry(string Name, string Status);
