using System.Security.Claims;
using System.Text;
using CourtBooking.Api.Data;
using CourtBooking.Api.Email;
using CourtBooking.Api.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Identity;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var auth = routes.MapGroup("/auth").WithTags("Auth");

        auth.MapGet("/privacy-policy", GetPrivacyPolicy);
        auth.MapPost("/register", RegisterAsync);
        auth.MapPost("/verify-email", VerifyEmailAsync);
        auth.MapPost("/login", LoginAsync);
        auth.MapPost("/logout", LogoutAsync).RequireAuthorization();
        auth.MapGet("/me", GetCurrentUserAsync).RequireAuthorization();
        auth.MapPut("/me/language", ChangeLanguageAsync).RequireAuthorization();

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

        var user = new AppUser
        {
            UserName = request.Email,
            Email = request.Email,
            PhoneNumber = request.PhoneNumber,
            Language = language,
        };

        var result = await userManager.CreateAsync(user, request.Password);
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

            await SendVerificationEmailAsync(user, userManager, emailSender, options.Value, cancellationToken);
            return TypedResults.Created("/api/auth/me");
        }

        // An address that is already registered answers exactly like a fresh one and gets an email instead,
        // so this endpoint cannot be used to find out who has an account (PDPA, PRD 8).
        var codes = result.Errors.Select(error => error.Code).ToArray();
        if (codes.Any(code => code is "DuplicateUserName" or "DuplicateEmail"))
        {
            await emailSender.SendAsync(
                new EmailMessage(
                    request.Email,
                    language,
                    "CourtPaka: account already exists",
                    "Someone tried to register with this address. If it was you, sign in or reset your password."),
                cancellationToken);
            return TypedResults.Created("/api/auth/me");
        }

        // Identity owns the rules, so its error decides the code the frontend shows.
        var failureCode = codes.Any(code => code is "InvalidEmail" or "InvalidUserName")
            ? AuthErrorCodes.InvalidEmail
            : AuthErrorCodes.WeakPassword;
        return ApiProblem.Of(StatusCodes.Status400BadRequest, failureCode);
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
        if (!result.Succeeded)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidVerificationToken);
        }

        // Sessions created before verification must stop claiming the account is unverified.
        await userManager.UpdateSecurityStampAsync(user);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        SignInManager<AppUser> signInManager)
    {
        var result = await signInManager.PasswordSignInAsync(
            request.Email, request.Password, isPersistent: true, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            return ApiProblem.Of(StatusCodes.Status423Locked, AuthErrorCodes.AccountLocked);
        }

        return result.Succeeded
            ? TypedResults.NoContent()
            : ApiProblem.Of(StatusCodes.Status401Unauthorized, AuthErrorCodes.InvalidCredentials);
    }

    private static async Task<NoContent> LogoutAsync(SignInManager<AppUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CurrentUserResponse>, NotFound>> GetCurrentUserAsync(
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager)
    {
        // Read through to the row: verification state gates booking, so a stale copy is not good enough.
        var user = await userManager.GetUserAsync(principal);
        return user is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new CurrentUserResponse(user.Id, user.Email!, user.EmailConfirmed, user.Language));
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

        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return TypedResults.NotFound();
        }

        var updated = await database.Users
            .Where(user => user.Id == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.Language, request.Language), cancellationToken);

        return updated == 1 ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task SendVerificationEmailAsync(
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

        await emailSender.SendAsync(
            new EmailMessage(
                user.Email!,
                user.Language,
                "CourtPaka: verify your email",
                $"Confirm your address to finish signing up: {link}"),
            cancellationToken);
    }
}
