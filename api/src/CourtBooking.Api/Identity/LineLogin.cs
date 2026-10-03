using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Identity;

/// <summary>
/// The LINE Login channel this deployment signs people in with (PRD US-01, D6). Both values come
/// from LINE Developers; the secret lives in the deployment's environment, never in the repo.
/// Without them the feature is off and the sign-in screens do not offer it.
/// </summary>
public sealed class LineOptions
{
    public string? ChannelId { get; init; }

    public string? ChannelSecret { get; init; }

    /// <summary>
    /// The Messaging API channel access token, which is what lets the platform push a message to
    /// somebody rather than only sign them in (PRD US-34). A deployment without one goes on
    /// writing to everybody by email, and nothing has to be switched off.
    /// </summary>
    public string? MessagingToken { get; init; }

    /// <summary>
    /// A stand-in LINE for local stacks, so the flow can be walked without a channel. Read only
    /// on a Development host, like the seeded accounts: it signs anybody in as anybody.
    /// </summary>
    public bool UseDevelopmentFake { get; init; }
}

/// <summary>Who LINE says signed in. <see cref="Subject"/> is the stable LINE user id.</summary>
public sealed record LineIdentity(string Subject, string? Name, string? Email);

/// <summary>
/// The two things the platform asks of LINE: where to send the person, and who came back.
/// An interface so a local stack and the tests can stand in for LINE itself.
/// </summary>
public interface ILineLogin
{
    bool IsEnabled { get; }

    /// <summary>LINE's authorization page for this attempt (OAuth 2.0 code flow with PKCE).</summary>
    string AuthorizeUrl(string state, string nonce, string codeChallenge, string redirectUri);

    /// <summary>
    /// Trades the code LINE sent back for the person it belongs to, or null when LINE does not
    /// vouch for it: a used or expired code, a token for another channel, or another nonce.
    /// </summary>
    Task<LineIdentity?> RedeemAsync(
        string code, string codeVerifier, string redirectUri, string nonce, CancellationToken cancellationToken);
}

/// <summary>
/// LINE Login v2.1. The ID token is checked by LINE's own verify endpoint, which checks the
/// signature, issuer, audience, expiry and nonce — so the platform holds no keys of LINE's and
/// cannot get one of those checks subtly wrong.
/// </summary>
public sealed class LineLoginClient(HttpClient http, IOptions<AppOptions> options, ILogger<LineLoginClient> logger)
    : ILineLogin
{
    public const string AuthorizeEndpoint = "https://access.line.me/oauth2/v2.1/authorize";
    public const string TokenEndpoint = "https://api.line.me/oauth2/v2.1/token";
    public const string VerifyEndpoint = "https://api.line.me/oauth2/v2.1/verify";

    private LineOptions Line => options.Value.Line;

    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Line.ChannelId) && !string.IsNullOrWhiteSpace(Line.ChannelSecret);

    public string AuthorizeUrl(string state, string nonce, string codeChallenge, string redirectUri) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            AuthorizeEndpoint,
            new Dictionary<string, string?>
            {
                ["response_type"] = "code",
                ["client_id"] = Line.ChannelId,
                ["redirect_uri"] = redirectUri,
                ["state"] = state,
                // Email is asked for, not required: LINE grants it per channel, and a person may
                // decline it on the consent screen.
                ["scope"] = "openid profile email",
                ["nonce"] = nonce,
                ["code_challenge"] = codeChallenge,
                ["code_challenge_method"] = "S256",
            });

    public async Task<LineIdentity?> RedeemAsync(
        string code, string codeVerifier, string redirectUri, string nonce, CancellationToken cancellationToken)
    {
        using var tokenResponse = await http.PostAsync(
            TokenEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = Line.ChannelId!,
                ["client_secret"] = Line.ChannelSecret!,
                ["code_verifier"] = codeVerifier,
            }),
            cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            // The body names the reason (a reused code, a wrong secret); it carries no token.
            logger.LogWarning(
                "LINE refused the code: {Status} {Body}",
                (int)tokenResponse.StatusCode,
                await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
            return null;
        }

        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        if (string.IsNullOrEmpty(token?.IdToken))
        {
            logger.LogWarning("LINE answered the code without an ID token");
            return null;
        }

        using var verifyResponse = await http.PostAsync(
            VerifyEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["id_token"] = token.IdToken,
                ["client_id"] = Line.ChannelId!,
                ["nonce"] = nonce,
            }),
            cancellationToken);
        if (!verifyResponse.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "LINE did not verify the ID token: {Status} {Body}",
                (int)verifyResponse.StatusCode,
                await verifyResponse.Content.ReadAsStringAsync(cancellationToken));
            return null;
        }

        var claims = await verifyResponse.Content.ReadFromJsonAsync<VerifiedToken>(cancellationToken);
        return string.IsNullOrEmpty(claims?.Sub)
            ? null
            : new LineIdentity(claims.Sub, claims.Name, string.IsNullOrWhiteSpace(claims.Email) ? null : claims.Email);
    }

    private sealed record TokenResponse([property: JsonPropertyName("id_token")] string? IdToken);

    private sealed record VerifiedToken(
        [property: JsonPropertyName("sub")] string? Sub,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("email")] string? Email);
}
