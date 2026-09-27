using System.Security.Claims;
using CourtBooking.Api.Bookings;
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
    private VenuePermissionRequirement(
        VenuePermissions permission,
        bool ownerOnly,
        bool whateverTheVenueSStatus = false,
        bool evenWhenTurnedAway = false,
        bool evenWhenSuspended = false)
    {
        Permission = permission;
        OwnerOnly = ownerOnly;
        WhateverTheVenueSStatus = whateverTheVenueSStatus;
        EvenWhenTurnedAway = evenWhenTurnedAway;
        EvenWhenSuspended = evenWhenSuspended;
    }

    public VenuePermissions Permission { get; }

    public bool OwnerOnly { get; }

    /// <summary>
    /// Whether this is still allowed at a venue that has been suspended or refused. Said outright
    /// rather than inferred from asking for no permission: a venue that is frozen changes in no
    /// way (PRD US-20), and the next thing mapped behind <see cref="Member"/> should have to
    /// declare that it is an exception rather than inherit one.
    /// </summary>
    public bool WhateverTheVenueSStatus { get; }

    /// <summary>Any member of the venue, whatever their permissions. Reading only.</summary>
    public static VenuePermissionRequirement Member { get; } =
        new(VenuePermissions.None, ownerOnly: false, whateverTheVenueSStatus: true);

    /// <summary>
    /// A member changing something about themselves rather than about the venue — what they want
    /// in their own inbox (PRD US-17). Allowed at a frozen venue on purpose: a venue that has
    /// been suspended is exactly where somebody might want the mail to stop.
    /// </summary>
    public static VenuePermissionRequirement OwnChoice { get; } =
        new(VenuePermissions.None, ownerOnly: false, whateverTheVenueSStatus: true);

    /// <summary>
    /// Whether the owner of a venue the platform turned away may still do this. A refused venue
    /// is otherwise frozen, but refusing it and then forbidding it from answering the refusal
    /// would be a door with no handle on either side (PRD US-10). Suspended stays frozen: that
    /// one is the platform holding a working venue still, not asking it a question.
    /// </summary>
    public bool EvenWhenTurnedAway { get; }

    /// <summary>
    /// Whether this still works at a suspended venue. A suspension stops a venue selling; it does
    /// not undo what it already sold. Bookings taken before it still have to be honoured or
    /// cancelled with a reason, and slips already sent still have to be looked at, so the doors
    /// that do those things stay open while the ones that would take new money do not
    /// (PRD US-20). Said per endpoint, because the difference between the two is the whole rule.
    /// </summary>
    public bool EvenWhenSuspended { get; }

    /// <summary>Actions the owner may not delegate: membership, tax identity, document voiding (PRD US-14).</summary>
    public static VenuePermissionRequirement Owner { get; } = new(VenuePermissions.None, ownerOnly: true);

    /// <summary>
    /// The owner putting right what the platform turned the venue away for, and asking again
    /// (PRD US-10). Owner's alone because it is the venue's tax identity (PRD US-14).
    /// </summary>
    public static VenuePermissionRequirement OwnerAnsweringRefusal { get; } =
        new(VenuePermissions.None, ownerOnly: true, evenWhenTurnedAway: true);

    public static VenuePermissionRequirement Needs(VenuePermissions permission) => new(permission, ownerOnly: false);

    /// <summary>
    /// A permission that keeps working while the venue is suspended, for the work a suspension
    /// does not cancel: finishing what was already sold (PRD US-20).
    /// </summary>
    public static VenuePermissionRequirement NeedsEvenWhenSuspended(VenuePermissions permission) =>
        new(permission, ownerOnly: false, evenWhenSuspended: true);
}

/// <summary>
/// The policies venue endpoints are mapped with. Declaring the right one at map time is what keeps
/// an endpoint from being reachable by the wrong member (PRD US-14).
/// </summary>
public static class VenuePolicies
{
    /// <summary>Any member of the venue, whatever their permissions. For reading.</summary>
    public static Action<AuthorizationPolicyBuilder> Member =>
        policy => policy.RequireAuthenticatedUser().AddRequirements(VenuePermissionRequirement.Member);

    /// <summary>A member changing what they themselves want, not what the venue is (PRD US-17).</summary>
    public static Action<AuthorizationPolicyBuilder> OwnChoice =>
        policy => policy.RequireAuthenticatedUser().AddRequirements(VenuePermissionRequirement.OwnChoice);

    public static Action<AuthorizationPolicyBuilder> OwnerOnly =>
        policy => policy.RequireAuthenticatedUser().AddRequirements(VenuePermissionRequirement.Owner);

    /// <summary>The owner fixing what a refusal was about, and asking again (PRD US-10).</summary>
    public static Action<AuthorizationPolicyBuilder> OwnerAnsweringRefusal =>
        policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(VenuePermissionRequirement.OwnerAnsweringRefusal);

    /// <summary>Courts, opening hours, prices and the cancellation policy (PRD US-11).</summary>
    public static Action<AuthorizationPolicyBuilder> Settings => Needs(VenuePermissions.ManageSettings);

    public static Action<AuthorizationPolicyBuilder> Needs(VenuePermissions permission) =>
        policy => policy.RequireAuthenticatedUser().AddRequirements(VenuePermissionRequirement.Needs(permission));

    /// <summary>
    /// For the doors a suspended venue still has to be able to open: honouring or cancelling
    /// what it already sold, and checking slips already sent (PRD US-20).
    /// </summary>
    public static Action<AuthorizationPolicyBuilder> NeedsEvenWhenSuspended(
        VenuePermissions permission) =>
        policy => policy
            .RequireAuthenticatedUser()
            .AddRequirements(VenuePermissionRequirement.NeedsEvenWhenSuspended(permission));
}

/// <summary>
/// The membership the current request runs under. Populated once by the authorization handler, so
/// endpoints do not query it again.
/// </summary>
public sealed class CurrentVenue
{
    public VenueMembership? Membership { get; set; }

    public VenueStatus? Status { get; set; }

    /// <summary>
    /// How long this venue waits before somebody has not turned up (PRD US-24). Carried with the
    /// request because the handler has already read the venue; asking again would be a second
    /// query for one integer.
    /// </summary>
    public int GraceMinutes { get; set; } = VenueDecisions.DefaultGraceMinutes;

    public VenueMembership Require() =>
        Membership ?? throw new InvalidOperationException("No venue membership was resolved for this request.");
}

public sealed class VenuePermissionHandler(AppDbContext database, CurrentVenue currentVenue)
    : AuthorizationHandler<VenuePermissionRequirement>
{
    public const string VenueRouteValue = "venueId";

    /// <summary>
    /// The door that stays open at a suspended venue (PRD US-20): the member holds the
    /// permission, and the venue is either trading or only suspended — suspension stops selling,
    /// not reading what was sold. Public so a page that reads across every venue a person has
    /// (badPaka 2c) asks the same question the route policy does, not a copy of it.
    /// </summary>
    public static bool ReadsEvenWhenSuspended(
        VenueMembership membership, VenueStatus? status, VenuePermissions permission) =>
        membership.Allows(permission)
        && (!VenueStatusRules.IsFrozen(status) || status == VenueStatus.Suspended);

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
            currentVenue.GraceMinutes = found?.Venue?.GraceMinutes ?? VenueDecisions.DefaultGraceMinutes;
        }

        var membership = currentVenue.Membership;
        if (membership is null)
        {
            return;
        }

        var readOnlyVenue = VenueStatusRules.IsFrozen(currentVenue.Status);

        var turnedAway = currentVenue.Status == VenueStatus.Rejected;

        var allowed = requirement switch
        {
            { OwnerOnly: true, EvenWhenTurnedAway: true } =>
                membership.Role == VenueRole.Owner && (!readOnlyVenue || turnedAway),
            { OwnerOnly: true } => membership.Role == VenueRole.Owner && !readOnlyVenue,
            { WhateverTheVenueSStatus: true } => true,
            { Permission: VenuePermissions.None } => !readOnlyVenue,
            { EvenWhenSuspended: true } needed =>
                ReadsEvenWhenSuspended(membership, currentVenue.Status, needed.Permission),
            var needed => membership.Allows(needed.Permission) && !readOnlyVenue,
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }
    }
}
