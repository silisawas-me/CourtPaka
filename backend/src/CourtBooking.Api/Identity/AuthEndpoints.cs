using System.Security.Claims;
using System.Text;
using CourtBooking.Api.Data;
using CourtBooking.Api.Email;
using CourtBooking.Api.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Identity;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var auth = routes.MapGroup("/auth").WithTags("Auth");

        auth.MapGet("/privacy-policy", GetPrivacyPolicy);
        auth.MapPost("/register", RegisterAsync).RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/resend-verification", ResendVerificationAsync).RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/verify-email", VerifyEmailAsync).RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/login", LoginAsync).RequireRateLimiting(RateLimitPolicies.Auth);
        auth.MapPost("/logout", LogoutAsync).RequireAuthorization();
        auth.MapGet("/me", GetCurrentUserAsync).RequireAuthorization();
        auth.MapPut("/me/language", ChangeLanguageAsync).RequireAuthorization();
        auth.MapPut("/me/phone", ChangePhoneAsync).RequireAuthorization();
        auth.MapLineLoginEndpoints();
        // Rate-limited like sign-in: it takes a password (PDPA, PRD 8).
        auth.MapPost("/me/delete", AccountDeletion.DeleteAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Auth);

        return auth;
    }

    /// <summary>The version the SPA must present when registering, so the server owns what "accepted" means.</summary>
    private static Ok<PrivacyPolicyResponse> GetPrivacyPolicy(IOptions<AppOptions> options) =>
        TypedResults.Ok(new PrivacyPolicyResponse(options.Value.PrivacyPolicyVersion));

    private static async Task<Results<Created, ProblemHttpResult>> RegisterAsync(
        RegisterRequest request,
        UserManager<AppUser> userManager,
        AppDbContext database,
        ITransactionalEmailSender emailSender,
        IOptions<AppOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var language = request.Language ?? SupportedLanguages.Default;
        if (!SupportedLanguages.IsSupported(language))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.UnsupportedLanguage);
        }

        if (request.PrivacyPolicyVersion != options.Value.PrivacyPolicyVersion)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, AuthErrorCodes.PrivacyPolicyOutdated);
        }

        // An account signed up here signs in with its address, so it must have one. (A LINE
        // account may not; EmailRules lets that through, which is why it is asked here.)
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidEmail);
        }

        string? phone = null;
        if (!string.IsNullOrWhiteSpace(request.PhoneNumber)
            && (phone = PhoneNumbers.Normalize(request.PhoneNumber)) is null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidPhone);
        }

        var user = new AppUser
        {
            UserName = request.Email,
            Email = request.Email,
            PhoneNumber = phone,
            Language = language,
        };

        // The account and its consent row are one unit: a half-registered user with no recorded
        // consent would violate PDPA (PRD 8) and could never be completed.
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        IdentityResult result;
        try
        {
            result = await userManager.CreateAsync(user, request.Password);
            if (result.Succeeded)
            {
                database.UserConsents.Add(new UserConsent
                {
                    UserId = user.Id,
                    Type = ConsentType.PrivacyPolicy,
                    Version = options.Value.PrivacyPolicyVersion,
                    AcceptedAt = timeProvider.GetUtcNow(),
                });
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                await SendVerificationEmailAsync(user, userManager, emailSender, options.Value, CancellationToken.None);
                return TypedResults.Created("/api/auth/me");
            }
        }
        catch (DbUpdateException exception) when (DbErrors.IsUniqueViolation(exception))
        {
            // Two registrations for the same address raced past the uniqueness check.
            await transaction.RollbackAsync(cancellationToken);
            await SendAccountExistsEmailAsync(request.Email, language, userManager, emailSender, CancellationToken.None);
            return TypedResults.Created("/api/auth/me");
        }

        await transaction.RollbackAsync(cancellationToken);

        // An address that is already registered answers exactly like a fresh one and gets an email instead,
        // so this endpoint cannot be used to find out who has an account (PDPA, PRD 8).
        var codes = result.Errors.Select(error => error.Code).ToArray();
        if (codes.Any(code => code is "DuplicateUserName" or "DuplicateEmail"))
        {
            await SendAccountExistsEmailAsync(request.Email, language, userManager, emailSender, CancellationToken.None);
            return TypedResults.Created("/api/auth/me");
        }

        // Identity owns the rules, so its error decides the code the frontend shows.
        if (codes.Any(code => code is "InvalidEmail" or "InvalidUserName" or "DuplicateEmail"))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidEmail);
        }

        if (codes.Any(code => code.StartsWith("Password", StringComparison.Ordinal)))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.WeakPassword);
        }

        // Anything else (store failures, concurrency) is ours, not the caller's.
        return ApiProblem.Of(StatusCodes.Status500InternalServerError, AuthErrorCodes.RegistrationFailed);
    }

    /// <summary>
    /// Sends the verification link again. Always answers the same way, so it cannot be used to find
    /// out which addresses have an account.
    /// </summary>
    private static async Task<Accepted> ResendVerificationAsync(
        ResendVerificationRequest request,
        UserManager<AppUser> userManager,
        ITransactionalEmailSender emailSender,
        IOptions<AppOptions> options,
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is { EmailConfirmed: false })
        {
            await SendVerificationEmailAsync(user, userManager, emailSender, options.Value, cancellationToken);
        }

        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> VerifyEmailAsync(
        VerifyEmailRequest request,
        UserManager<AppUser> userManager)
    {
        var user = await userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidVerificationToken);
        }

        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Token));
        }
        catch (FormatException)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidVerificationToken);
        }

        var result = await userManager.ConfirmEmailAsync(user, token);

        // Verification state is read from the row on every request, so open sessions see it at once
        // and do not have to be invalidated (which would sign the user out mid-flow).
        return result.Succeeded
            ? TypedResults.NoContent()
            : ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidVerificationToken);
    }

    /// <summary>What a developer types to be the owner of the mock branches (see AppOptions.DevQuickLogin).</summary>
    public const string QuickLoginWord = "1";

    private static async Task<Results<NoContent, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        SignInManager<AppUser> signInManager,
        UserManager<AppUser> userManager,
        ITransactionalEmailSender emailSender,
        IOptions<AppOptions> options,
        IWebHostEnvironment environment,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        // A local stack's shortcut: "1" / "1" is the owner of the mock branches. Two locks, as for
        // the seed — the flag AND a development host — so a deployment cannot open it by accident.
        if (options.Value.DevQuickLogin
            && environment.IsDevelopment()
            && request.Email == QuickLoginWord
            && request.Password == QuickLoginWord
            && (await userManager.FindByEmailAsync(DevelopmentMockData.DemoEmail)
                ?? await userManager.FindByEmailAsync(DevelopmentSeeder.OwnerEmail)) is { } owner)
        {
            loggers.CreateLogger(typeof(AuthEndpoints)).LogWarning(
                "Development quick login as the mock owner");
            await signInManager.SignInAsync(owner, isPersistent: true);
            return TypedResults.NoContent();
        }

        var result = await signInManager.PasswordSignInAsync(
            request.Email, request.Password, isPersistent: true, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            return TypedResults.NoContent();
        }

        // A lockout only ever happens to an address that has an account, so answering differently
        // would turn this endpoint into a way to find out who is a member (PDPA, PRD 8).
        // The account owner is told by email instead.
        if (result.IsLockedOut)
        {
            var user = await userManager.FindByEmailAsync(request.Email);
            if (user is not null)
            {
                var (subject, body) = AccountLetters.Locked(user.Language);
                await emailSender.SendAsync(
                    new EmailMessage(
                        user.Email!, user.Language, subject, body, AccountLetters.LockedTemplate),
                    cancellationToken);
            }
        }

        // Identity refuses a suspended account before it looks at the password (NotAllowed), so
        // the password is checked here — and a wrong one counts towards the lockout as usual.
        // Only somebody who knows the password is told the account is suspended; anybody else
        // gets the same answer as for an address with no account (PDPA, PRD 8).
        if (result.IsNotAllowed
            && await userManager.FindByEmailAsync(request.Email) is { SuspendedAt: not null } suspended
            && !await userManager.IsLockedOutAsync(suspended))
        {
            if (await userManager.CheckPasswordAsync(suspended, request.Password))
            {
                return ApiProblem.Of(StatusCodes.Status403Forbidden, AuthErrorCodes.AccountSuspended);
            }

            await userManager.AccessFailedAsync(suspended);
        }

        return ApiProblem.Of(StatusCodes.Status401Unauthorized, AuthErrorCodes.InvalidCredentials);
    }

    private static async Task<NoContent> LogoutAsync(SignInManager<AppUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CurrentUserResponse>, NotFound>> GetCurrentUserAsync(
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager,
        IOptions<AppOptions> options)
    {
        // Read through to the row: verification state gates booking, so a stale copy is not good enough.
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        var signsInWithLine = (await userManager.GetLoginsAsync(user))
            .Any(login => login.LoginProvider == LineLoginEndpoints.Provider);

        return TypedResults.Ok(new CurrentUserResponse(
            user.Id,
            user.Email,
            user.EmailConfirmed,
            user.Language,
            PlatformAdmins.Includes(user, options.Value),
            user.PhoneNumber,
            user.PasswordHash is not null,
            signsInWithLine,
            BookingEligibility.MissingFor(user.EmailConfirmed, signsInWithLine, user.PhoneNumber)));
    }

    /// <summary>
    /// The number a venue can reach the booker on (PRD US-01). Empty clears it — which, for a LINE
    /// account without a proved address, is also giving up booking until it is set again.
    /// </summary>
    private static async Task<Results<NoContent, ProblemHttpResult, NotFound>> ChangePhoneAsync(
        ChangePhoneRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        string? phone = null;
        if (!string.IsNullOrWhiteSpace(request.PhoneNumber)
            && (phone = PhoneNumbers.Normalize(request.PhoneNumber)) is null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidPhone);
        }

        if (CallerId.TryOf(principal) is not { } userId)
        {
            return TypedResults.NotFound();
        }

        // Straight to the row, like the language: the stamp is not touched, so no session ends.
        var updated = await database.Users
            .Where(user => user.Id == userId && user.DeletedAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(user => user.PhoneNumber, phone)
                    .SetProperty(user => user.PhoneNumberConfirmed, false),
                cancellationToken);

        return updated == 1 ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, ProblemHttpResult, NotFound>> ChangeLanguageAsync(
        ChangeLanguageRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        if (!SupportedLanguages.IsSupported(request.Language))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.UnsupportedLanguage);
        }

        if (CallerId.TryOf(principal) is not { } userId)
        {
            return TypedResults.NotFound();
        }

        var updated = await database.Users
            .Where(user => user.Id == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.Language, request.Language), cancellationToken);

        return updated == 1 ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    internal static async Task SendVerificationEmailAsync(
        AppUser user,
        UserManager<AppUser> userManager,
        ITransactionalEmailSender emailSender,
        AppOptions options,
        CancellationToken cancellationToken)
    {
        var rawToken = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var link = QueryHelpers.AddQueryString(
            $"{options.BaseUrl.TrimEnd('/')}/verify-email",
            new Dictionary<string, string?>
            {
                ["userId"] = user.Id.ToString(),
                ["token"] = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(rawToken)),
            });

        var (subject, body) = AccountLetters.Verify(user.Language, link);
        await emailSender.SendAsync(
            new EmailMessage(user.Email!, user.Language, subject, body, AccountLetters.VerifyTemplate),
            cancellationToken);
    }

    /// <summary>Warns the address owner in the language of their account, not of whoever tried to register.</summary>
    private static async Task SendAccountExistsEmailAsync(
        string email,
        string fallbackLanguage,
        UserManager<AppUser> userManager,
        ITransactionalEmailSender emailSender,
        CancellationToken cancellationToken)
    {
        var owner = await userManager.FindByEmailAsync(email);
        var language = owner?.Language ?? fallbackLanguage;
        var (subject, body) = AccountLetters.AccountExists(language);

        await emailSender.SendAsync(
            new EmailMessage(email, language, subject, body, AccountLetters.AccountExistsTemplate),
            cancellationToken);
    }

}
