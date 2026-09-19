using System.Security.Claims;
using CourtBooking.Api.Data;
using CourtBooking.Api.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>
/// "May this user do X at the venue in the route?" — the one question every venue endpoint asks.
/// Global roles cannot express it, because the answer differs per venue (PRD US-14, 9.2).
/// </summary>
public sealed class VenuePermissionRequirement : IAuthorizationRequirement
{
    private VenuePermissionRequirement(VenuePermissions permission, bool ownerOnly)
    {
        Permission = permission;
        OwnerOnly = ownerOnly;
    }

    public VenuePermissions Permission { get; }

    public bool OwnerOnly { get; }

    /// <summary>Any member of the venue, whatever their permissions.</summary>
    public static VenuePermissionRequirement Member { get; } = new(VenuePermissions.None, ownerOnly: false);

    /// <summary>Actions the owner may not delegate: membership, tax identity, document voiding (PRD US-14).</summary>
    public static VenuePermissionRequirement Owner { get; } = new(VenuePermissions.None, ownerOnly: true);

    public static VenuePermissionRequirement Needs(VenuePermissions permission) => new(permission, ownerOnly: false);
}

/// <summary>
/// The policies venue endpoints are mapped with. Declaring the right one at map time is what keeps
/// an endpoint from being reachable by the wrong member (PRD US-14).
/// </summary>
public static class VenuePolicies
{
    /// <summary>Any member of the venue, whatever their permissions.</summary>
    public static Action<AuthorizationPolicyBuilder> Member =>
        policy => policy.RequireAuthenticatedUser().AddRequirements(VenuePermissionRequirement.Member);

    public static Action<AuthorizationPolicyBuilder> OwnerOnly =>
        policy => policy.RequireAuthenticatedUser().AddRequirements(VenuePermissionRequirement.Owner);

    /// <summary>Courts, opening hours, prices and the cancellation policy (PRD US-11).</summary>
    public static Action<AuthorizationPolicyBuilder> Settings => Needs(VenuePermissions.ManageSettings);

    public static Action<AuthorizationPolicyBuilder> Needs(VenuePermissions permission) =>
        policy => policy.RequireAuthenticatedUser().AddRequirements(VenuePermissionRequirement.Needs(permission));
}

/// <summary>
/// The membership the current request runs under. Populated once by the authorization handler, so
/// endpoints do not query it again.
/// </summary>
public sealed class CurrentVenue
{
    public VenueMembership? Membership { get; set; }

    public VenueStatus? Status { get; set; }

    public VenueMembership Require() =>
        Membership ?? throw new InvalidOperationException("No venue membership was resolved for this request.");
}

public sealed class VenuePermissionHandler(AppDbContext database, CurrentVenue currentVenue)
    : AuthorizationHandler<VenuePermissionRequirement>
{
    public const string VenueRouteValue = "venueId";

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        VenuePermissionRequirement requirement)
    {
        // In endpoint routing the resource is the HttpContext, so the route value is right here.
        if (context.Resource is not HttpContext httpContext)
        {
            return;
        }

        if (CallerId.TryOf(context.User) is not { } userId
            || !Guid.TryParse(httpContext.Request.RouteValues[VenueRouteValue]?.ToString(), out var venueId))
        {
            return;
        }

        if (currentVenue.Membership is null)
        {
            var found = await database.VenueMemberships
                .AsNoTracking()
                .Include(member => member.Venue)
                .SingleOrDefaultAsync(member => member.VenueId == venueId && member.UserId == userId);

            currentVenue.Membership = found;
            currentVenue.Status = found?.Venue?.Status;
        }

        var membership = currentVenue.Membership;
        if (membership is null)
        {
            return;
        }

        var readOnlyVenue = VenueStatusRules.IsFrozen(currentVenue.Status);

        var allowed = requirement switch
        {
            { OwnerOnly: true } => membership.Role == VenueRole.Owner && !readOnlyVenue,
            { Permission: VenuePermissions.None } => true,
            var needed => membership.Allows(needed.Permission) && !readOnlyVenue,
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }
    }
}
