namespace CourtBooking.Api.Identity;

public sealed record RegisterRequest(
    string Email,
    string Password,
    string PrivacyPolicyVersion,
    string? Language,
    string? PhoneNumber);

public sealed record LoginRequest(string Email, string Password);

public sealed record VerifyEmailRequest(Guid UserId, string Token);

public sealed record ResendVerificationRequest(string Email);

public sealed record ChangeLanguageRequest(string Language);

public sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    bool EmailConfirmed,
    string Language,
    /// <summary>
    /// Whether this person acts for the platform (PRD US-20). Said here so the app knows which
    /// doors to draw — not so it can decide anything: every one of those doors asks again.
    /// </summary>
    bool IsPlatformAdmin);

public sealed record PrivacyPolicyResponse(string Version);

/// <summary>
/// Stable codes the frontend translates (PRD US-23); messages are never shown to users directly.
/// </summary>
public static class AuthErrorCodes
{
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string InvalidVerificationToken = "auth.invalid_verification_token";
    public const string UnsupportedLanguage = "auth.unsupported_language";
    public const string WeakPassword = "auth.weak_password";
    public const string InvalidEmail = "auth.invalid_email";
    public const string PrivacyPolicyOutdated = "auth.privacy_policy_outdated";
    public const string RegistrationFailed = "auth.registration_failed";

    /// <summary>Signing in is allowed unverified; booking is not (PRD US-01).</summary>
    public const string EmailNotVerified = "auth.email_not_verified";
}

public static class RateLimitPolicies
{
    public const string Auth = "auth";

    /// <summary>Ten uploads an hour per person (PRD 8, Security).</summary>
    public const string Upload = "upload";
}
