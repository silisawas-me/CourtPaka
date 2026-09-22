namespace CourtBooking.Api.Identity;

public sealed record RegisterRequest(
    string? Email,
    string Password,
    string PrivacyPolicyVersion,
    string? Language,
    string? PhoneNumber);

public sealed record LoginRequest(string Email, string Password);

public sealed record VerifyEmailRequest(Guid UserId, string Token);

public sealed record ResendVerificationRequest(string Email);

public sealed record ChangeLanguageRequest(string Language);

public sealed record ChangePhoneRequest(string? PhoneNumber);

public sealed record CurrentUserResponse(
    Guid Id,
    /// <summary>Null for a LINE account that shared no address, or shared one somebody else has.</summary>
    string? Email,
    bool EmailConfirmed,
    string Language,
    /// <summary>
    /// Whether this person acts for the platform (PRD US-20). Said here so the app knows which
    /// doors to draw — not so it can decide anything: every one of those doors asks again.
    /// </summary>
    bool IsPlatformAdmin,
    string? PhoneNumber = null,
    /// <summary>False for a LINE account: deleting it is confirmed through LINE instead.</summary>
    bool HasPassword = true,
    bool SignsInWithLine = false,
    /// <summary>What the account still needs before it can book (BookingEligibility), or null.</summary>
    string? CannotBookBecause = null);

public sealed record PrivacyPolicyResponse(string Version);

/// <summary>
/// Stable codes the frontend translates (PRD US-23); messages are never shown to users directly.
/// </summary>
public static class AuthErrorCodes
{
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string AccountSuspended = "auth.account_suspended";
    public const string InvalidVerificationToken = "auth.invalid_verification_token";
    public const string UnsupportedLanguage = "auth.unsupported_language";
    public const string WeakPassword = "auth.weak_password";
    public const string InvalidEmail = "auth.invalid_email";
    public const string PrivacyPolicyOutdated = "auth.privacy_policy_outdated";
    public const string RegistrationFailed = "auth.registration_failed";

    /// <summary>Signing in is allowed unverified; booking is not (PRD US-01).</summary>
    public const string EmailNotVerified = "auth.email_not_verified";

    /// <summary>
    /// A LINE account books once it has given a phone number, which is how a venue reaches a
    /// booker who may have no address here (PRD US-01).
    /// </summary>
    public const string PhoneRequired = "auth.phone_required";

    public const string InvalidPhone = "auth.invalid_phone";
}

public static class RateLimitPolicies
{
    public const string Auth = "auth";

    /// <summary>Ten uploads an hour per person (PRD 8, Security).</summary>
    public const string Upload = "upload";
}
