using System.Security.Claims;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CourtBooking.Api.Venues;

public static class VenueEndpoints
{
    public static RouteGroupBuilder MapVenueEndpoints(this IEndpointRouteBuilder routes)
    {
        var venues = routes.MapGroup("/venues").WithTags("Venues").RequireAuthorization();

        venues.MapPost("/", CreateAsync);
        venues.MapGet("/mine", ListMineAsync);

        var venue = venues.MapGroup("/{venueId:guid}");
        venue.MapGet("/", GetAsync).RequireAuthorization(VenuePolicies.Member);
        venue.MapPut("/", RenameAsync).RequireAuthorization(VenuePolicies.For(VenuePermissions.ManageSettings));
        venue.MapGet("/members", ListMembersAsync).RequireAuthorization(VenuePolicies.Member);
        venue.MapPost("/members", InviteMemberAsync).RequireAuthorization(VenuePolicies.Member);
        venue.MapPut("/members/{userId:guid}/permissions", ChangePermissionsAsync)
            .RequireAuthorization(VenuePolicies.Member);
        venue.MapDelete("/members/{userId:guid}", RemoveMemberAsync).RequireAuthorization(VenuePolicies.Member);

        return venues;
    }

    private static async Task<Results<Created<VenueResponse>, ProblemHttpResult>> CreateAsync(
        CreateVenueRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var ownerId = UserId(principal);
        var venue = new Venue
        {
            Code = request.Code.Trim().ToUpperInvariant(),
            Name = request.Name.Trim(),
            OwnerId = ownerId,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        database.Venues.Add(venue);
        // The person who applies runs the venue, so they start as its owner (PRD US-10).
        database.VenueMemberships.Add(new VenueMembership
        {
            VenueId = venue.Id,
            UserId = ownerId,
            Role = VenueRole.Owner,
            Permissions = VenuePermissions.All,
            CreatedAt = timeProvider.GetUtcNow(),
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.CodeAlreadyUsed);
        }

        return TypedResults.Created($"/api/venues/{venue.Id}", ToResponse(venue));
    }

    private static async Task<Ok<VenueResponse[]>> ListMineAsync(
        ClaimsPrincipal principal,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var userId = UserId(principal);
        var venues = await database.VenueMemberships
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Join(database.Venues, member => member.VenueId, v => v.Id, (_, v) => v)
            .OrderBy(v => v.Name)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(venues.Select(ToResponse).ToArray());
    }

    private static async Task<Results<Ok<VenueResponse>, NotFound>> GetAsync(
        Guid venueId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var venue = await database.Venues.AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == venueId, cancellationToken);

        return venue is null ? TypedResults.NotFound() : TypedResults.Ok(ToResponse(venue));
    }

    private static async Task<Results<NoContent, NotFound>> RenameAsync(
        Guid venueId,
        RenameVenueRequest request,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var updated = await database.Venues
            .Where(venue => venue.Id == venueId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(venue => venue.Name, request.Name.Trim()),
                cancellationToken);

        return updated == 1 ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Ok<VenueMemberResponse[]>> ListMembersAsync(
        Guid venueId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var members = await database.VenueMemberships
            .AsNoTracking()
            .Where(member => member.VenueId == venueId)
            .Include(member => member.User)
            .OrderBy(member => member.CreatedAt)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(members.Select(ToResponse).ToArray());
    }

    private static async Task<Results<Created, ProblemHttpResult, ForbidHttpResult>> InviteMemberAsync(
        Guid venueId,
        InviteMemberRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        UserManager<AppUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await IsOwnerAsync(venueId, principal, database, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var permissions = request.Permissions ?? VenuePermissions.StaffDefault;
        if ((permissions & ~VenuePermissions.All) != 0)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidPermissions);
        }

        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, VenueErrorCodes.UnknownUser);
        }

        if (await database.VenueMemberships.AnyAsync(
                member => member.VenueId == venueId && member.UserId == user.Id, cancellationToken))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.AlreadyMember);
        }

        database.VenueMemberships.Add(new VenueMembership
        {
            VenueId = venueId,
            UserId = user.Id,
            Role = VenueRole.Staff,
            Permissions = permissions,
            CreatedAt = timeProvider.GetUtcNow(),
        });
        await database.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/venues/{venueId}/members");
    }

    private static async Task<Results<NoContent, ProblemHttpResult, NotFound, ForbidHttpResult>> ChangePermissionsAsync(
        Guid venueId,
        Guid userId,
        ChangePermissionsRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        if (!await IsOwnerAsync(venueId, principal, database, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        if ((request.Permissions & ~VenuePermissions.All) != 0)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidPermissions);
        }

        var membership = await database.VenueMemberships
            .SingleOrDefaultAsync(member => member.VenueId == venueId && member.UserId == userId, cancellationToken);

        if (membership is null)
        {
            return TypedResults.NotFound();
        }

        // The owner always holds every permission; changing that would lock the venue out of itself.
        if (membership.Role == VenueRole.Owner)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.OwnerCannotBeChanged);
        }

        membership.Permissions = request.Permissions;
        await database.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult, NotFound, ForbidHttpResult>> RemoveMemberAsync(
        Guid venueId,
        Guid userId,
        ClaimsPrincipal principal,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        if (!await IsOwnerAsync(venueId, principal, database, cancellationToken))
        {
            return TypedResults.Forbid();
        }

        var membership = await database.VenueMemberships
            .SingleOrDefaultAsync(member => member.VenueId == venueId && member.UserId == userId, cancellationToken);

        if (membership is null)
        {
            return TypedResults.NotFound();
        }

        if (membership.Role == VenueRole.Owner)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.OwnerCannotBeChanged);
        }

        database.VenueMemberships.Remove(membership);
        await database.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static Task<bool> IsOwnerAsync(
        Guid venueId,
        ClaimsPrincipal principal,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var userId = UserId(principal);
        return database.VenueMemberships.AnyAsync(
            member => member.VenueId == venueId && member.UserId == userId && member.Role == VenueRole.Owner,
            cancellationToken);
    }

    private static Guid UserId(ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static VenueResponse ToResponse(Venue venue) =>
        new(venue.Id, venue.Code, venue.Name, venue.Status.ToString());

    private static VenueMemberResponse ToResponse(VenueMembership membership) =>
        new(
            membership.UserId,
            membership.User?.Email ?? string.Empty,
            membership.Role.ToString(),
            Enum.GetValues<VenuePermissions>()
                .Where(permission =>
                    permission is not (VenuePermissions.None or VenuePermissions.StaffDefault or VenuePermissions.All)
                    && membership.Allows(permission))
                .Select(permission => permission.ToString())
                .ToArray());
}
