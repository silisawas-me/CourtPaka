using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace CourtBooking.Api.Identity;

/// <summary>
/// A stand-in for LINE on a local stack, so signing in with LINE can be walked — by a person or
/// by scripts/verify/line_login.py — without a LINE channel (PRD US-01).
///
/// Its page lets whoever opens it be any LINE user they type. That is the point locally and a
/// disaster anywhere else, so it is only wired up on a Development host with
/// <c>App:Line:UseDevelopmentFake</c> on — two locks, like the seeded accounts.
///
/// The code it hands back is the identity itself, sealed, so there is no server-side store and
/// no call from the API back to itself. It checks the nonce like LINE does; it does not check the
/// PKCE verifier, which only the real LINE is in a position to.
/// </summary>
public sealed class DevelopmentLineLogin(IDataProtectionProvider protection) : ILineLogin
{
    private const string AuthorizePath = "/api/dev/line/authorize";

    private readonly ITimeLimitedDataProtector codes =
        protection.CreateProtector("CourtPaka.DevelopmentLine.v1").ToTimeLimitedDataProtector();

    private sealed record Grant(string Subject, string? Name, string? Email, string Nonce);

    public bool IsEnabled => true;

    public string AuthorizeUrl(string state, string nonce, string codeChallenge, string redirectUri) =>
        QueryHelpers.AddQueryString(
            AuthorizePath,
            new Dictionary<string, string?>
            {
                ["state"] = state,
                ["nonce"] = nonce,
                ["redirect_uri"] = redirectUri,
            });

    public Task<LineIdentity?> RedeemAsync(
        string code, string codeVerifier, string redirectUri, string nonce, CancellationToken cancellationToken)
    {
        try
        {
            var grant = JsonSerializer.Deserialize<Grant>(codes.Unprotect(code));
            return Task.FromResult(
                grant is not null && grant.Nonce == nonce
                    ? new LineIdentity(grant.Subject, grant.Name, grant.Email)
                    : null);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return Task.FromResult<LineIdentity?>(null);
        }
    }

    /// <summary>LINE's consent screen, as a form: who to be, and allow or deny.</summary>
    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet(AuthorizePath, (string state, string nonce, string redirect_uri) =>
        {
            string Encode(string value) => WebUtility.HtmlEncode(value);
            var html = $"""
                <!doctype html>
                <html lang="en"><head><meta charset="utf-8"><title>Development LINE</title></head>
                <body>
                <h1>Development LINE</h1>
                <p>Not LINE. Local stacks only: sign in as anybody.</p>
                <form method="post">
                  <input type="hidden" name="state" value="{Encode(state)}">
                  <input type="hidden" name="nonce" value="{Encode(nonce)}">
                  <input type="hidden" name="redirect_uri" value="{Encode(redirect_uri)}">
                  <label>LINE user id <input name="sub" id="sub" required></label>
                  <label>Display name <input name="name" id="name"></label>
                  <label>Email <input name="email" id="email"></label>
                  <button name="decision" value="allow" id="allow">Allow</button>
                  <button name="decision" value="deny" id="deny">Deny</button>
                </form>
                </body></html>
                """;
            return Results.Content(html, "text/html; charset=utf-8");
        });

        routes.MapPost(AuthorizePath, (HttpRequest request, DevelopmentLineLogin line) =>
        {
            var form = request.Form;
            var redirectUri = form["redirect_uri"].ToString();

            // Back to this site's own callback only, as LINE only returns to a registered URL.
            if (!redirectUri.EndsWith("/api/auth/line/callback", StringComparison.Ordinal))
            {
                return Results.BadRequest();
            }

            var answer = new Dictionary<string, string?> { ["state"] = form["state"].ToString() };
            if (form["decision"] == "allow")
            {
                var grant = new Grant(
                    form["sub"].ToString(),
                    NullIfEmpty(form["name"]),
                    NullIfEmpty(form["email"]),
                    form["nonce"].ToString());
                answer["code"] = line.codes.Protect(JsonSerializer.Serialize(grant), TimeSpan.FromMinutes(10));
            }
            else
            {
                answer["error"] = "access_denied";
            }

            return Results.Redirect(QueryHelpers.AddQueryString(redirectUri, answer));
        }).DisableAntiforgery();
    }

    private static string? NullIfEmpty(Microsoft.Extensions.Primitives.StringValues value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.ToString();
}
