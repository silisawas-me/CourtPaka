using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CourtBooking.Api.Data;
using CourtBooking.Api.Email;
using CourtBooking.Api.Http;
using CourtBooking.Api.Observability;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Identity;

public sealed record LineAvailabilityResponse(bool Enabled);

public sealed record LinePendingResponse(string? Name, string? Email);

public sealed record CompleteLineSignUpRequest(string PrivacyPolicyVersion, string? Language, string? PhoneNumber);

public static class LineErrorCodes
{
    /// <summary>LINE did not vouch for the person, or the attempt was not the one this browser started.</summary>
    public const string Failed = "auth.line_failed";

    /// <summary>The person said no on LINE's consent screen.</summary>
    public const string Denied = "auth.line_denied";

    /// <summary>The LINE sign-up waited too long between LINE and the consent step; start again.</summary>
    public const string Expired = "auth.line_expired";

    /// <summary>The LINE account that confirmed is not the one this account signs in with.</summary>
    public const string Mismatch = "auth.line_mismatch";

    /// <summary>This LINE account already has an account here; signing in is the way in.</summary>
    public const string AlreadyRegistered = "auth.line_already_registered";

    public const string Disabled = "auth.line_disabled";
}

/// <summary>
/// Signing in with LINE (PRD US-01, D6).
///
/// The first time, LINE's answer is not an account yet: the person still has to accept the privacy
/// policy (PDPA, PRD 8), which LINE's consent screen is not. So who LINE said they are waits in a
/// short-lived encrypted cookie until they accept it on the platform's own page, and only then is
/// the account made. Every time after that, LINE's answer signs them straight in.
///
/// An address LINE shares is kept, unverified, and verified the platform's own way before anything
/// is sent to it — and only if no account has it already. It is never used to join an existing
/// account: that would hand an account to whoever controls a LINE account with the same address.
///
/// Accounts made this way have no password, so the same flow run again stands in for one where a
/// password would be asked again: deleting the account (<see cref="AccountDeletion"/>).
/// </summary>
public static class LineLoginEndpoints
{
    public const string Provider = "LINE";

    private const string FlowCookie = "cp_line_flow";
    private const string PendingCookie = "cp_line_pending";
    public const string ConfirmedCookie = "cp_line_confirmed";

    private const string ProtectorPurpose = "CourtPaka.LineLogin.v1";

    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ConfirmedLifetime = TimeSpan.FromMinutes(5);

    public static void MapLineLoginEndpoints(this RouteGroupBuilder auth)
    {
        var line = auth.MapGroup("/line");

        line.MapGet("", (ILineLogin lineLogin) => TypedResults.Ok(new LineAvailabilityResponse(lineLogin.IsEnabled)));
        line.MapGet("/start", Start).RequireRateLimiting(RateLimitPolicies.Auth);
        // Proving again who you are is a POST that carries the session cookie, which a link from
        // another site cannot make the browser send (SameSite=Lax). A GET could be followed from
        // anywhere, and would hand out five minutes of "I re-authenticated" nobody asked for.
        line.MapPost("/start", Start)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .DisableAntiforgery();
        line.MapGet("/callback", CallbackAsync).RequireRateLimiting(RateLimitPolicies.Auth);
        line.MapGet("/pending", Pending);
        line.MapPost("/complete", CompleteAsync).RequireRateLimiting(RateLimitPolicies.Auth);
    }

    /// <summary>What one attempt has to remember between leaving for LINE and coming back.</summary>
    private sealed record Flow(string State, string Nonce, string Verifier, string ReturnUrl, bool Confirming);

    /// <summary>Who LINE said came back, waiting for them to accept the privacy policy.</summary>
    private sealed record PendingSignUp(string Subject, string? Name, string? Email, string ReturnUrl);

    private sealed record Confirmation(Guid UserId);

    /// <summary>
    /// Sends the browser to LINE. <c>purpose=confirm</c> is a signed-in person proving again that
    /// they are the LINE account this account signs in with.
    /// </summary>
    private static IResult Start(
        string? returnUrl,
        string? purpose,
        HttpContext http,
        ILineLogin lineLogin,
        IDataProtectionProvider protection,
        IOptions<AppOptions> options)
    {
        if (!lineLogin.IsEnabled)
        {
            return Results.Redirect($"/login?line={LineErrorCodes.Disabled}");
        }

        // Only the POST carries a session, so only it can be a re-authentication.
        var confirming = purpose == "confirm" && HttpMethods.IsPost(http.Request.Method);

        var flow = new Flow(
            State: Random(),
            Nonce: Random(),
            Verifier: Random(),
            ReturnUrl: LocalOnly(returnUrl),
            Confirming: confirming);

        Write(http, options.Value, FlowCookie, flow, FlowLifetime, protection);

        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(flow.Verifier)));
        return Results.Redirect(
            lineLogin.AuthorizeUrl(flow.State, flow.Nonce, challenge, RedirectUri(options.Value)));
    }

    private static async Task<IResult> CallbackAsync(
        string? code,
        string? state,
        string? error,
        HttpContext http,
        ILineLogin lineLogin,
        IDataProtectionProvider protection,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        IOptions<AppOptions> options,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        // Read once and gone: a callback cannot be replayed against the same attempt.
        var flow = Read<Flow>(http, FlowCookie, protection);
        Delete(http, options.Value, FlowCookie);

        // The state is what ties this answer to the attempt this browser started (CSRF): without
        // it, anybody could send a victim's browser back here with the attacker's own LINE code.
        if (flow is null
            || state is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(flow.State), Encoding.UTF8.GetBytes(state)))
        {
            return Results.Redirect($"/login?line={LineErrorCodes.Failed}");
        }

        var failedTo = flow.Confirming ? "/account" : "/login";

        if (error is not null || code is null)
        {
            return Results.Redirect($"{failedTo}?line={(string.Equals(error, "access_denied", StringComparison.OrdinalIgnoreCase) ? LineErrorCodes.Denied : LineErrorCodes.Failed)}");
        }

        // The code is spent and the flow cookie is gone, so there is nothing to retry: whatever
        // went wrong on LINE's side has to come back as something the app can say (US-23).
        LineIdentity? identity;
        try
        {
            identity = await lineLogin.RedeemAsync(
                code, flow.Verifier, RedirectUri(options.Value), flow.Nonce, cancellationToken);
        }
        catch (Exception exception)
            when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            loggers.CreateLogger(typeof(LineLoginEndpoints)).LogError(
                exception, "Could not read LINE's answer for a sign-in");
            return Results.Redirect($"{failedTo}?line={LineErrorCodes.Failed}");
        }

        if (identity is null)
        {
            return Results.Redirect($"{failedTo}?line={LineErrorCodes.Failed}");
        }

        var user = await users.FindByLoginAsync(Provider, identity.Subject);

        if (flow.Confirming)
        {
            return await ConfirmAsync(http, user, protection, users, options.Value);
        }

        if (user is null)
        {
            // Not an account yet: it becomes one on the consent page.
            Write(
                http,
                options.Value,
                PendingCookie,
                new PendingSignUp(identity.Subject, identity.Name, identity.Email, flow.ReturnUrl),
                PendingLifetime,
                protection);
            return Results.Redirect(
                QueryHelpers.AddQueryString("/register/line", "returnUrl", flow.ReturnUrl));
        }

        // The same rules as a password sign-in: a suspended or deleted account does not get in.
        // The person has just proved they are this LINE account, so they can be told why.
        if (!await signIn.CanSignInAsync(user))
        {
            var why = user.SuspendedAt is not null ? AuthErrorCodes.AccountSuspended : AccountGate.Closed;
            return Results.Redirect($"/login?line={why}");
        }

        await signIn.SignInAsync(user, isPersistent: true, authenticationMethod: Provider);
        AppEvents.For(loggers).LogInformation("line_signed_in {UserId}", user.Id);
        return Results.Redirect(flow.ReturnUrl);
    }

    /// <summary>
    /// A signed-in person came back from LINE as the LINE account their account signs in with.
    /// Remembered for a few minutes, for the one thing that asks for it: deleting the account.
    /// </summary>
    private static async Task<IResult> ConfirmAsync(
        HttpContext http,
        AppUser? cameBackAs,
        IDataProtectionProvider protection,
        UserManager<AppUser> users,
        AppOptions options)
    {
        var signedIn = await users.GetUserAsync(http.User);
        if (signedIn is null || cameBackAs is null || cameBackAs.Id != signedIn.Id)
        {
            return Results.Redirect($"/account?line={LineErrorCodes.Mismatch}");
        }

        Write(http, options, ConfirmedCookie, new Confirmation(signedIn.Id), ConfirmedLifetime, protection, "/api/auth");
        return Results.Redirect("/account?line=confirmed");
    }

    /// <summary>Who is waiting to finish signing up, for the consent page to greet.</summary>
    private static Results<Ok<LinePendingResponse>, ProblemHttpResult> Pending(
        HttpContext http, IDataProtectionProvider protection)
    {
        return Read<PendingSignUp>(http, PendingCookie, protection) is { } pending
            ? TypedResults.Ok(new LinePendingResponse(pending.Name, pending.Email))
            : ApiProblem.Of(StatusCodes.Status404NotFound, LineErrorCodes.Expired);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> CompleteAsync(
        CompleteLineSignUpRequest request,
        HttpContext http,
        IDataProtectionProvider protection,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        AppDbContext database,
        ITransactionalEmailSender emails,
        IOptions<AppOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (Read<PendingSignUp>(http, PendingCookie, protection) is not { } pending)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, LineErrorCodes.Expired);
        }

        var language = request.Language ?? SupportedLanguages.Default;
        if (!SupportedLanguages.IsSupported(language))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.UnsupportedLanguage);
        }

        // LINE signs up bookers only, and nobody signs up by themselves any more (owner-complete 3a).
        if (!options.Value.OpenSignUp)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, AuthErrorCodes.InvitationRequired);
        }

        if (request.PrivacyPolicyVersion != options.Value.PrivacyPolicyVersion)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, AuthErrorCodes.PrivacyPolicyOutdated);
        }

        string? phone = null;
        if (!string.IsNullOrWhiteSpace(request.PhoneNumber)
            && (phone = PhoneNumbers.Normalize(request.PhoneNumber)) is null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidPhone);
        }

        // An address LINE shared is kept only if it is an address and nobody has it; it is never
        // proof of owning an account that does. Kept unverified until the person opens the
        // platform's own link. Anything else is dropped rather than failing the sign-up, which
        // would leave them retrying the same refusal until the pending cookie ran out.
        var email = pending.Email is { } shared
            && new EmailAddressAttribute().IsValid(shared)
            && await users.FindByEmailAsync(shared) is null
                ? shared
                : null;

        var user = new AppUser
        {
            // The LINE user id is unique and stable; nobody types it, so it only has to be unique.
            UserName = $"line-{pending.Subject}",
            Email = email,
            PhoneNumber = phone,
            Language = language,
        };

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                return created.Errors.Any(e => e.Code == "DuplicateUserName")
                    // The same LINE account finished twice (two tabs); the first one won.
                    ? ApiProblem.Of(StatusCodes.Status409Conflict, LineErrorCodes.AlreadyRegistered)
                    : ApiProblem.Of(StatusCodes.Status500InternalServerError, AuthErrorCodes.RegistrationFailed);
            }

            var linked = await users.AddLoginAsync(user, new UserLoginInfo(Provider, pending.Subject, Provider));
            if (!linked.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ApiProblem.Of(StatusCodes.Status409Conflict, LineErrorCodes.AlreadyRegistered);
            }

            database.UserConsents.Add(new UserConsent
            {
                UserId = user.Id,
                Type = ConsentType.PrivacyPolicy,
                Version = options.Value.PrivacyPolicyVersion,
                AcceptedAt = timeProvider.GetUtcNow(),
            });
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DbErrors.IsUniqueViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status409Conflict, LineErrorCodes.AlreadyRegistered);
        }

        Delete(http, options.Value, PendingCookie);
        await signIn.SignInAsync(user, isPersistent: true, authenticationMethod: Provider);
        AppEvents.For(loggers).LogInformation("line_registered {UserId} {SharedEmail}", user.Id, email is not null);

        if (email is not null)
        {
            // After the commit, and never the reason the sign-up fails: the account is made.
            try
            {
                await AuthEndpoints.SendVerificationEmailAsync(user, users, emails, options.Value, CancellationToken.None);
            }
            catch (Exception exception)
            {
                loggers.CreateLogger(typeof(LineLoginEndpoints)).LogError(
                    exception, "Could not send the verification email to LINE account {UserId}", user.Id);
            }
        }

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Whether this person came back from LINE, as this account, within the last few minutes.
    /// Read by <see cref="AccountDeletion"/> for an account that has no password to ask for.
    /// </summary>
    public static bool RecentlyConfirmed(HttpContext http, Guid userId, IDataProtectionProvider protection) =>
        Read<Confirmation>(http, ConfirmedCookie, protection)?.UserId == userId;

    public static void ForgetConfirmation(HttpContext http, AppOptions options) =>
        Delete(http, options, ConfirmedCookie, "/api/auth");

    private static string RedirectUri(AppOptions options) =>
        $"{options.BaseUrl.TrimEnd('/')}/api/auth/line/callback";

    /// <summary>A path on this site, or the home page: never somewhere else (open redirect).</summary>
    private static string LocalOnly(string? returnUrl) =>
        returnUrl is { Length: > 0 } url && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal)
            && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";

    private static string Random() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>One purpose per cookie, so one kind can never be passed off as another.</summary>
    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider protection, string cookie) =>
        protection.CreateProtector(ProtectorPurpose, cookie).ToTimeLimitedDataProtector();

    private static void Write<T>(
        HttpContext http,
        AppOptions options,
        string name,
        T value,
        TimeSpan lifetime,
        IDataProtectionProvider protection,
        string path = "/api/auth/line")
    {
        var sealedValue = Protector(protection, name).Protect(JsonSerializer.Serialize(value), lifetime);
        http.Response.Cookies.Append(name, sealedValue, Cookie(options, path, lifetime));
    }

    private static T? Read<T>(HttpContext http, string name, IDataProtectionProvider protection)
        where T : class
    {
        if (http.Request.Cookies[name] is not { } raw)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(Protector(protection, name).Unprotect(raw));
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static void Delete(HttpContext http, AppOptions options, string name, string path = "/api/auth/line") =>
        http.Response.Cookies.Delete(name, Cookie(options, path, TimeSpan.Zero));

    /// <summary>
    /// Lax, because LINE's return is a top-level navigation from another site, which Strict would
    /// send without it.
    /// </summary>
    private static CookieOptions Cookie(AppOptions options, string path, TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = options.RequireSecureCookies,
        Path = path,
        MaxAge = lifetime,
        IsEssential = true,
    };
}
