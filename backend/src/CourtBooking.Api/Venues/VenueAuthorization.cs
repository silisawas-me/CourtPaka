using System.Security.Claims;
using CourtBooking.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>
/// "May this user do X at the venue in the route?" — the one question every venue endpoint asks.
/// Global roles cannot express it, because the answer differs per venue (PRD US-14, 9.2).
/// </summary>
public sealed class VenuePermissionRequirement(VenuePermissions permission) : IAuthorizationRequirement
{
    public VenuePermissions Permission { get; } = permission;
}

public sealed class VenuePermissionHandler(
    IHttpContextAccessor httpContextAccessor,
    AppDbContext database)
    : AuthorizationHandler<VenuePermissionRequirement>
{
    public const string VenueRouteValue = "venueId";

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        VenuePermissionRequirement requirement)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return;
        }

        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return;
        }

        if (!Guid.TryParse(httpContext.Request.RouteValues[VenueRouteValue]?.ToString(), out var venueId))
        {
            return;
        }

        var membership = await database.VenueMemberships
            .AsNoTracking()
            .SingleOrDefaultAsync(member => member.VenueId == venueId && member.UserId == userId);

        if (membership?.Allows(requirement.Permission) == true)
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Policy names map one to one onto permissions, so endpoints read as the rule they enforce.</summary>
public static class VenuePolicies
{
    public const string Prefix = "venue:";

    public static string For(VenuePermissions permission) => Prefix + permission;

    public static string Member => Prefix + nameof(VenuePermissions.None);

    public static IEnumerable<(string Name, VenuePermissions Permission)> All()
    {
        yield return (Member, VenuePermissions.None);
        foreach (var permission in Enum.GetValues<VenuePermissions>())
        {
            if (permission is VenuePermissions.None or VenuePermissions.StaffDefault or VenuePermissions.All)
            {
                continue;
            }

            yield return (For(permission), permission);
        }
    }
}
