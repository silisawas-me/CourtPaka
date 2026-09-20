using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
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
/// </summary>
public static class PlatformAdmins
{
    public const string PolicyName = "PlatformAdmin";

    /// <summary>
    /// Whether this caller acts for the platform. Matched on the address they signed in with,
    /// case-insensitively, because an email address is not case sensitive in the part that
    /// matters and nobody types their own the same way twice.
    /// </summary>
    public static bool Includes(ClaimsPrincipal principal, AppOptions options)
    {
        var email = principal.FindFirstValue(ClaimTypes.Email)
                    ?? principal.FindFirstValue(ClaimTypes.Name);

        return !string.IsNullOrWhiteSpace(email)
               && options.PlatformAdmins.Any(
                   admin => string.Equals(admin.Trim(), email, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>"Is this caller the platform?" — the one question that is not about a venue.</summary>
public sealed class PlatformAdminRequirement : IAuthorizationRequirement;

public sealed class PlatformAdminHandler(IOptions<AppOptions> options)
    : AuthorizationHandler<PlatformAdminRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PlatformAdminRequirement requirement)
    {
        if (PlatformAdmins.Includes(context.User, options.Value))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
