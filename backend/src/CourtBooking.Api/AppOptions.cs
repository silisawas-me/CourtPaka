using System.ComponentModel.DataAnnotations;

namespace CourtBooking.Api;

/// <summary>
/// Environment-specific values every environment must supply; validated at startup so a missing
/// value fails the deployment instead of producing broken links at send time.
/// </summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>Public origin of the site, used to build links in outgoing email.</summary>
    [Required]
    [Url]
    public required string BaseUrl { get; init; }

    /// <summary>Version of the privacy policy users must accept (PDPA, PRD 8).</summary>
    [Required]
    public required string PrivacyPolicyVersion { get; init; }

    /// <summary>Off only for local development and tests, which run over plain HTTP.</summary>
    public bool RequireSecureCookies { get; init; } = true;

    /// <summary>
    /// Local stacks migrate themselves so the app is usable straight after `docker compose up`.
    /// Deployments run the migration bundle from the deploy job instead (PRD 9.4).
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; init; }

    /// <summary>
    /// Creates a development venue with an owner and a staff account, so a local stack is usable
    /// straight away. No deployed environment turns this on.
    /// </summary>
    public bool SeedDevelopmentData { get; init; }

    /// <summary>
    /// Where the data protection key ring lives. Without it every restart signs users out,
    /// because the keys that encrypt the auth cookie are regenerated in memory.
    /// </summary>
    public string? DataProtectionKeysPath { get; init; }

    /// <summary>Requests per minute per client IP allowed on the unauthenticated auth endpoints.</summary>
    [Range(1, 10_000)]
    public int AuthRequestsPerMinute { get; init; } = 10;

    /// <summary>How often a sign-in cookie is re-checked against the user row (lockout, deletion, password change).</summary>
    [Range(0, 86_400)]
    public int SessionRevalidationSeconds { get; init; } = 900;
}
