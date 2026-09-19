using Microsoft.AspNetCore.Identity;

namespace CourtBooking.Api.Identity;

/// <summary>
/// Account for every human in the system: bookers, venue staff and platform admins (PRD 2).
/// </summary>
public sealed class AppUser : IdentityUser<Guid>
{
    /// <summary>UI and email language, "th" or "en" (PRD US-01, US-23).</summary>
    public string Language { get; set; } = SupportedLanguages.Default;

    public ICollection<UserConsent> Consents { get; } = [];
}

/// <summary>
/// Append-only record of what a user agreed to and when (PDPA, PRD 8). Never updated in place:
/// a new policy version produces a new row, so past consent stays auditable.
/// </summary>
public sealed class UserConsent
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid UserId { get; init; }

    public required ConsentType Type { get; init; }

    public required string Version { get; init; }

    public required DateTimeOffset AcceptedAt { get; init; }
}

public enum ConsentType
{
    PrivacyPolicy = 1,
}

public static class SupportedLanguages
{
    public const string Thai = "th";
    public const string English = "en";
    public const string Default = Thai;

    public static bool IsSupported(string? language) => language is Thai or English;
}
