using System.Text.Json;

namespace CourtBooking.Api.Tests.Infrastructure;

/// <summary>
/// The API answers failures with a stable code in the ProblemDetails body (PRD US-23), so tests
/// assert on that code rather than on a message. One reader, so the contract is parsed once.
/// </summary>
public static class ProblemDetailsExtensions
{
    public static async Task<string?> ErrorCodeAsync(this HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
