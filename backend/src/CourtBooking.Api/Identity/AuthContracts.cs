namespace CourtBooking.Api.Identity;

public sealed record RegisterRequest(
    string Email,
    string Password,
    string PrivacyPolicyVersion,
    string? Language,
    string? PhoneNumber);

public sealed record LoginRequest(string Email, string Password);

public sealed record VerifyEmailRequest(Guid UserId, string Token);

public sealed record ChangeLanguageRequest(string Language);

public sealed record CurrentUserResponse(Guid Id, string Email, bool EmailConfirmed, string Language);

public sealed record PrivacyPolicyResponse(string Version);

/// <summary>
/// Stable codes the frontend translates (PRD US-23); messages are never shown to users directly.
/// </summary>
public static class AuthErrorCodes
{
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string AccountLocked = "auth.account_locked";
    public const string InvalidVerificationToken = "auth.invalid_verification_token";
    public const string UnsupportedLanguage = "auth.unsupported_language";
    public const string WeakPassword = "auth.weak_password";
    public const string InvalidEmail = "auth.invalid_email";
    public const string PrivacyPolicyOutdated = "auth.privacy_policy_outdated";
}
