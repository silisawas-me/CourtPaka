using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Identity;

/// <summary>
/// Who acts for the platform rather than for a venue (PRD US-20, US-22).
///
/// Every other permission in this system is per venue, because that is where the work is. This
/// is the one that is not: approving a venue, or suspending one, is the platform acting on a
/// venue from outside it.
///
/// Read from configuration, and deliberately not from a column somebody can set. There is no
/// endpoint that grants it, so there is no path through the running application that turns a
/// booker into an admin — becoming one means having the deployment's configuration, which is a
/// different kind of access and a different set of people.
///
/// **And the address has to be proved.** Registration is open to anyone and takes whatever address
/// it is given, so a configured address with no account behind it yet is an invitation: register
/// it first, never open the confirmation mail, and be the platform. Confirming means reading the
/// inbox, which is the one thing a stranger who guessed the address cannot do. The same gate
/// already stands in front of something far smaller — taking a court-hour (PRD US-01).
/// </summary>
public static class PlatformAdmins
{
    public const string PolicyName = "PlatformAdmin";

    /// <summary>
    /// Whether this account acts for the platform. Decided from the user row rather than from the
    /// sign-in cookie's claims, so the answer is about the account as it stands — confirmed or
    /// not — and not about what a claim happened to hold when it was issued.
    ///
    /// Matched case-insensitively, because an email address is not case sensitive in the part
    /// that matters and nobody types their own the same way twice.
    /// </summary>
    public static bool Includes(AppUser? user, AppOptions options) =>
        user is { EmailConfirmed: true, Email: { } email }
        && !string.IsNullOrWhiteSpace(email)
        && options.PlatformAdmins.Any(
            admin => string.Equals(admin.Trim(), email, StringComparison.OrdinalIgnoreCase));
}

/// <summary>"Is this caller the platform?" — the one question that is not about a venue.</summary>
public sealed class PlatformAdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Scoped, because it reads the user row on every request it guards. That is a handful of admin
/// routes, and the alternative — trusting a claim stamped at sign-in — would keep an account an
/// admin until its cookie ran out, whatever had happened to it since.
/// </summary>
public sealed class PlatformAdminHandler(UserManager<AppUser> users, IOptions<AppOptions> options)
    : AuthorizationHandler<PlatformAdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlatformAdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (PlatformAdmins.Includes(await users.GetUserAsync(context.User), options.Value))
        {
            context.Succeed(requirement);
        }
    }
}
