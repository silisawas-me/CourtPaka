using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace CourtBooking.Api.Identity;

public static class AuthEndpoints
{
    public static RouteGroupBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var auth = routes.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/register", RegisterAsync);
        auth.MapPost("/verify-email", VerifyEmailAsync);
        auth.MapPost("/login", LoginAsync);
        auth.MapPost("/logout", LogoutAsync).RequireAuthorization();
        auth.MapGet("/me", GetCurrentUser).RequireAuthorization();
        auth.MapPut("/me/language", ChangeLanguageAsync).RequireAuthorization();

        return auth;
    }

    private static async Task<Results<Created, ProblemHttpResult>> RegisterAsync(
        RegisterRequest request,
        UserManager<AppUser> userManager,
        IEmailSender emailSender,
        IConfiguration configuration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!new EmailAddressAttribute().IsValid(request.Email))
        {
            return Failure(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidEmail);
        }

        if (string.IsNullOrWhiteSpace(request.PrivacyPolicyVersion))
        {
            return Failure(StatusCodes.Status400BadRequest, AuthErrorCodes.PrivacyPolicyRequired);
        }

        var language = request.Language ?? SupportedLanguages.Default;
        if (!SupportedLanguages.IsSupported(language))
        {
            return Failure(StatusCodes.Status400BadRequest, AuthErrorCodes.UnsupportedLanguage);
        }

        var user = new AppUser
        {
            UserName = request.Email,
            Email = request.Email,
            PhoneNumber = request.PhoneNumber,
            Language = language,
            PrivacyPolicyVersion = request.PrivacyPolicyVersion,
            PrivacyPolicyAcceptedAt = timeProvider.GetUtcNow(),
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (result.Succeeded)
        {
            await SendVerificationEmailAsync(user, userManager, emailSender, configuration, cancellationToken);
            return TypedResults.Created("/api/auth/me");
        }

        // An address that is already registered answers exactly like a fresh one and gets an email instead,
        // so this endpoint cannot be used to find out who has an account (PDPA, PRD 8).
        var alreadyRegistered = result.Errors.Any(error =>
            error.Code is "DuplicateUserName" or "DuplicateEmail");
        if (alreadyRegistered)
        {
            await SendAccountExistsEmailAsync(request.Email, emailSender, cancellationToken);
            return TypedResults.Created("/api/auth/me");
        }

        return Failure(StatusCodes.Status400BadRequest, AuthErrorCodes.WeakPassword);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> VerifyEmailAsync(
        VerifyEmailRequest request,
        UserManager<AppUser> userManager)
    {
        var user = await userManager.FindByIdAsync(request.UserId.ToString());
        if (user is null)
        {
            return Failure(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidVerificationToken);
        }

        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(request.Token));
        }
        catch (FormatException)
        {
            return Failure(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidVerificationToken);
        }

        var result = await userManager.ConfirmEmailAsync(user, token);
        return result.Succeeded
            ? TypedResults.NoContent()
            : Failure(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidVerificationToken);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        SignInManager<AppUser> signInManager)
    {
        var result = await signInManager.PasswordSignInAsync(
            request.Email, request.Password, isPersistent: true, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            return Failure(StatusCodes.Status423Locked, AuthErrorCodes.AccountLocked);
        }

        return result.Succeeded
            ? TypedResults.NoContent()
            : Failure(StatusCodes.Status401Unauthorized, AuthErrorCodes.InvalidCredentials);
    }

    private static async Task<NoContent> LogoutAsync(SignInManager<AppUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CurrentUserResponse>, NotFound>> GetCurrentUser(
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager)
    {
        var user = await userManager.GetUserAsync(principal);
        return user is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new CurrentUserResponse(user.Id, user.Email!, user.EmailConfirmed, user.Language));
    }

    private static async Task<Results<NoContent, ProblemHttpResult, NotFound>> ChangeLanguageAsync(
        ChangeLanguageRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager)
    {
        if (!SupportedLanguages.IsSupported(request.Language))
        {
            return Failure(StatusCodes.Status400BadRequest, AuthErrorCodes.UnsupportedLanguage);
        }

        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        user.Language = request.Language;
        await userManager.UpdateAsync(user);
        return TypedResults.NoContent();
    }

    private static async Task SendVerificationEmailAsync(
        AppUser user,
        UserManager<AppUser> userManager,
        IEmailSender emailSender,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var rawToken = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(rawToken));
        var baseUrl = configuration["App:BaseUrl"]?.TrimEnd('/') ?? "http://localhost:8080";
        var link = $"{baseUrl}/verify-email?userId={user.Id}&token={token}";

        await emailSender.SendAsync(
            user.Email!,
            "CourtPaka: verify your email",
            $"Confirm your address to finish signing up: {link}",
            cancellationToken);
    }

    private static Task SendAccountExistsEmailAsync(
        string email,
        IEmailSender emailSender,
        CancellationToken cancellationToken) =>
        emailSender.SendAsync(
            email,
            "CourtPaka: account already exists",
            "Someone tried to register with this address. If it was you, sign in instead or reset your password.",
            cancellationToken);

    private static ProblemHttpResult Failure(int statusCode, string code) =>
        TypedResults.Problem(statusCode: statusCode, extensions: new Dictionary<string, object?> { ["code"] = code });
}
