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
}
