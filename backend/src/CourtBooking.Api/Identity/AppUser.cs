using Microsoft.AspNetCore.Identity;

namespace CourtBooking.Api.Identity;

/// <summary>
/// Account for every human in the system: bookers, venue staff and platform admins (PRD 2).
/// </summary>
public sealed class AppUser : IdentityUser<Guid>
{
    /// <summary>UI and email language, "th" or "en" (PRD US-01, US-23).</summary>
    public string Language { get; set; } = SupportedLanguages.Default;

    /// <summary>Privacy policy version the user accepted at registration (PRD US-01, PDPA).</summary>
    public required string PrivacyPolicyVersion { get; set; }

    public required DateTimeOffset PrivacyPolicyAcceptedAt { get; set; }
}

public static class SupportedLanguages
{
    public const string Thai = "th";
    public const string English = "en";
    public const string Default = Thai;

    public static bool IsSupported(string? language) => language is Thai or English;
}
