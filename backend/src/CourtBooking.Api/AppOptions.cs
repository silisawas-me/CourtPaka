using CourtBooking.Api.Identity;
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

    /// <summary>
    /// Version of the agreement a venue accepts when it applies (PRD US-10, Q8). Its wording
    /// waits on the lawyer; the version does not, because a venue that accepted something has to
    /// be able to say which something, whenever that question is asked.
    /// </summary>
    [Required]
    public required string VenueAgreementVersion { get; init; }

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
    /// Four made-up branches with a fortnight of trade (DevelopmentMockData), so the owner app
    /// has something to look like locally. Development only, and only on top of the seed above.
    /// </summary>
    public bool SeedMockData { get; init; }

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

    /// <summary>
    /// Where uploaded payment slips are written. They leave the container's filesystem for object
    /// storage before this is in front of real venues (PRD 9.1); until then the path has to be a
    /// mounted volume, or a restart loses them.
    /// </summary>
    [Required]
    public required string SlipStoragePath { get; init; }

    /// <summary>
    /// The addresses that act for the platform rather than for a venue (PRD US-20, US-22).
    ///
    /// Configuration rather than a column, on purpose: there is no endpoint that grants this, so
    /// no path through the running application turns a booker into an admin. Empty is a valid
    /// deployment — one with nobody who can approve a venue, which is worth failing loudly about
    /// at the first application rather than quietly allowing anyone through.
    /// </summary>
    public string[] PlatformAdmins { get; init; } = [];

    /// <summary>
    /// Where the platform takes its commission (PRD US-21). Not required to start: a deployment
    /// that has not said yet can do everything but be paid, and an invoice says so rather than
    /// showing an empty line and looking broken.
    /// </summary>
    public string? PlatformPromptPayId { get; init; }

    public string? PlatformAccountName { get; init; }

    /// <summary>Uploads an hour per person (PRD 8, Security).</summary>
    [Range(1, 10_000)]
    public int UploadsPerHour { get; init; } = 10;

    /// <summary>
    /// How often the caretaker looks for holds that ran out and slips about to be played
    /// (PRD 9.2, US-17). Shorter than the reminder window, or a reminder can be missed entirely.
    /// </summary>
    [Range(5, 3_600)]
    public int CaretakerIntervalSeconds { get; init; } = 60;

    /// <summary>
    /// Off for tests, which drive the chores directly rather than waiting for a clock. Every
    /// deployed environment leaves it on: without it, a hold on a court nobody looks at keeps
    /// its hours forever.
    /// </summary>
    public bool RunCaretaker { get; init; } = true;

    /// <summary>Signing in with LINE (PRD US-01); off until a channel is configured.</summary>
    public LineOptions Line { get; init; } = new();
}
