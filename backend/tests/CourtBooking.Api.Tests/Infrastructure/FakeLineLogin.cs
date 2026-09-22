using System.Collections.Concurrent;
using CourtBooking.Api.Identity;

namespace CourtBooking.Api.Tests.Infrastructure;

/// <summary>
/// LINE, as far as the tests are concerned (PRD US-01): it hands out codes that stand for a LINE
/// account, and — like the real one — refuses a code whose nonce is not the one the attempt asked
/// with, so the replay the nonce exists to stop is a thing a test can try.
/// </summary>
public sealed class FakeLineLogin : ILineLogin
{
    public const string AuthorizePage = "https://line.test/authorize";

    private readonly ConcurrentDictionary<string, LineIdentity> granted = new();

    public bool IsEnabled { get; set; } = true;

    /// <summary>The nonce of the most recent attempt, as the real LINE would have been given it.</summary>
    public string? LastNonce { get; private set; }

    public string? LastChallenge { get; private set; }

    public string? LastRedirectUri { get; private set; }

    /// <summary>Makes a code that will come back as this person.</summary>
    public string Grant(string subject, string? name = "ผู้เล่น", string? email = null)
    {
        var code = Guid.NewGuid().ToString("N");
        granted[code] = new LineIdentity(subject, name, email);
        return code;
    }

    public string AuthorizeUrl(string state, string nonce, string codeChallenge, string redirectUri)
    {
        LastNonce = nonce;
        LastChallenge = codeChallenge;
        LastRedirectUri = redirectUri;
        return $"{AuthorizePage}?state={Uri.EscapeDataString(state)}";
    }

    public Task<LineIdentity?> RedeemAsync(
        string code, string codeVerifier, string redirectUri, string nonce, CancellationToken cancellationToken)
    {
        // A code is good once, and only for the attempt it was made for.
        if (nonce != LastNonce || !granted.TryRemove(code, out var identity))
        {
            return Task.FromResult<LineIdentity?>(null);
        }

        return Task.FromResult<LineIdentity?>(identity);
    }
}
