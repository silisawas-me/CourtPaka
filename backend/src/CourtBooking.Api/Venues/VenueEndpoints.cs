using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CourtBooking.Api.Data;
using CourtBooking.Api.Email;
using CourtBooking.Api.Http;
using CourtBooking.Api.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Venues;

public static class VenueEndpoints
{
    /// <summary>An invitation has to outlive a weekend but not a month (PRD US-14).</summary>
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    public static RouteGroupBuilder MapVenueEndpoints(this IEndpointRouteBuilder routes)
    {
        var venues = routes.MapGroup("/venues").WithTags("Venues").RequireAuthorization();

        venues.MapPost("/", CreateAsync);
        venues.MapGet("/mine", ListMineAsync);
        venues.MapPost("/invitations/accept", AcceptInvitationAsync);

        var venue = venues.MapGroup("/{venueId:guid}");
        venue.MapGet("/", Get).RequireAuthorization(VenuePolicies.Member);
        venue.MapPut("/", UpdateDetailsAsync).RequireAuthorization(VenuePolicies.Settings);
        venue.MapGet("/members", ListMembersAsync).RequireAuthorization(VenuePolicies.Member);
        venue.MapGet("/invitations", ListInvitationsAsync).RequireAuthorization(VenuePolicies.OwnerOnly);
        venue.MapPost("/invitations", InviteAsync).RequireAuthorization(VenuePolicies.OwnerOnly);
        venue.MapPut("/members/{userId:guid}/permissions", ChangePermissionsAsync).RequireAuthorization(VenuePolicies.OwnerOnly);
        venue.MapDelete("/members/{userId:guid}", RemoveMemberAsync).RequireAuthorization(VenuePolicies.OwnerOnly);

        venue.MapCourtEndpoints();
        venue.MapPricingEndpoints();

        return venues;
    }

    private static async Task<Results<Created<VenueResponse>, ProblemHttpResult>> CreateAsync(
        CreateVenueRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var invalid = VenueValidation.ValidateCode(request.Code)
                      ?? VenueValidation.ValidateName(request.Name)
                      ?? VenueValidation.ValidateAddress(
                          request.AddressLine, request.District, request.Province);
        if (invalid is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        var now = timeProvider.GetUtcNow();
        var venue = new Venue
        {
            Code = request.Code.Trim().ToUpperInvariant(),
            Name = request.Name.Trim(),
            AddressLine = request.AddressLine.Trim(),
            District = request.District.Trim(),
            Province = request.Province.Trim(),
            CreatedAt = now,
        };

        database.Venues.Add(venue);
        // The terms a venue starts with are a row like any other, so a booking made on day one has
        // a policy to point at rather than a default resolved somewhere else (PRD S-11, BR-05).
        database.CancellationPolicies.Add(CancellationPolicy.Create(
            venue.Id, CancellationPolicy.Default, CallerId.Of(principal), now));
        // The person who applies runs the venue, so they start as its owner (PRD US-10).
        database.VenueMemberships.Add(new VenueMembership
        {
            VenueId = venue.Id,
            UserId = CallerId.Of(principal),
            Role = VenueRole.Owner,
            Permissions = VenuePermissions.None, // Owners derive their permissions from the role.
            CreatedAt = now,
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DbErrors.IsUniqueViolation(exception))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.CodeAlreadyUsed);
        }

        return TypedResults.Created(
            $"/api/venues/{venue.Id}", ToResponse(venue, VenueRole.Owner, VenuePermissions.None));
    }

    private static async Task<Ok<VenueResponse[]>> ListMineAsync(
        ClaimsPrincipal principal,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var userId = CallerId.Of(principal);
        var memberships = await database.VenueMemberships
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Include(member => member.Venue)
            .OrderBy(member => member.Venue!.Name)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(memberships
            .Select(member => ToResponse(member.Venue!, member.Role, member.Permissions))
            .ToArray());
    }

    private static Ok<VenueResponse> Get(CurrentVenue currentVenue)
    {
        // Authorization already loaded the venue and the caller's membership for this request.
        var membership = currentVenue.Require();
        return TypedResults.Ok(ToResponse(membership.Venue!, membership.Role, membership.Permissions));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> UpdateDetailsAsync(
        Guid venueId,
        UpdateVenueRequest request,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var invalid = VenueValidation.ValidateName(request.Name)
                      ?? VenueValidation.ValidateAddress(
                          request.AddressLine, request.District, request.Province);
        if (invalid is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalid);
        }

        var updated = await database.Venues
            .Where(venue => venue.Id == venueId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(venue => venue.Name, request.Name.Trim())
                    .SetProperty(venue => venue.AddressLine, request.AddressLine.Trim())
                    .SetProperty(venue => venue.District, request.District.Trim())
                    .SetProperty(venue => venue.Province, request.Province.Trim()),
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

    private static async Task<Ok<VenueInvitationResponse[]>> ListInvitationsAsync(
        Guid venueId,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var invitations = await database.VenueInvitations
            .AsNoTracking()
            .Where(invitation =>
                invitation.VenueId == venueId && invitation.AcceptedAt == null && invitation.ExpiresAt > now)
            .OrderBy(invitation => invitation.CreatedAt)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(invitations
            .Select(invitation => new VenueInvitationResponse(
                invitation.Id, invitation.Email, VenuePermissionSet.Describe(invitation.Permissions), invitation.ExpiresAt))
            .ToArray());
    }

    /// <summary>
    /// Invites an address, which may not have an account yet: the link in the email is what grants
    /// access, once the person signs in and accepts it (PRD US-14).
    /// </summary>
    private static async Task<Results<Created<VenueInvitationResponse>, ProblemHttpResult>> InviteAsync(
        Guid venueId,
        InviteMemberRequest request,
        AppDbContext database,
        CurrentVenue currentVenue,
        UserManager<AppUser> userManager,
        ITransactionalEmailSender emailSender,
        IOptions<AppOptions> options,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        VenuePermissions permissions;
        if (request.Permissions is null)
        {
            permissions = VenuePermissions.StaffDefault;
        }
        else if (!VenuePermissionSet.TryParse(request.Permissions, out permissions))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidPermissions);
        }

        var invalidEmail = VenueValidation.ValidateEmail(request.Email);
        if (invalidEmail is not null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, invalidEmail);
        }

        var email = request.Email.Trim();
        var normalizedEmail = Normalize(email);
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null
            && await database.VenueMemberships.AnyAsync(
                member => member.VenueId == venueId && member.UserId == existing.Id, cancellationToken))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.AlreadyMember);
        }

        var now = timeProvider.GetUtcNow();

        // One live invitation per address: re-inviting replaces the old link instead of leaving
        // several valid tokens the owner cannot revoke. Matching is on the normalized address, so
        // a difference in capitalisation cannot leave an older, more permissive link alive.
        await database.VenueInvitations
            .Where(item =>
                item.VenueId == venueId && item.NormalizedEmail == normalizedEmail && item.AcceptedAt == null)
            .ExecuteDeleteAsync(cancellationToken);

        var token = GenerateToken();
        var invitation = new VenueInvitation
        {
            VenueId = venueId,
            Email = email,
            NormalizedEmail = normalizedEmail,
            Permissions = permissions,
            TokenHash = HashToken(token),
            ExpiresAt = now + InvitationLifetime,
            CreatedAt = now,
        };

        database.VenueInvitations.Add(invitation);
        await database.SaveChangesAsync(cancellationToken);

        var link = QueryHelpers.AddQueryString(
            $"{options.Value.BaseUrl.TrimEnd('/')}/venue-invitation",
            new Dictionary<string, string?> { ["invitationId"] = invitation.Id.ToString(), ["token"] = token });

        await emailSender.SendAsync(
            new EmailMessage(
                email,
                existing?.Language ?? SupportedLanguages.Default,
                $"CourtPaka: you were invited to {currentVenue.Require().Venue!.Name}",
                $"Accept the invitation within 7 days: {link}"),
            cancellationToken);

        return TypedResults.Created(
            $"/api/venues/{venueId}/invitations",
            new VenueInvitationResponse(
                invitation.Id, email, VenuePermissionSet.Describe(permissions), invitation.ExpiresAt));
    }

    private static async Task<Results<Ok<VenueResponse>, ProblemHttpResult>> AcceptInvitationAsync(
        AcceptInvitationRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        UserManager<AppUser> userManager,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var invitation = await database.VenueInvitations
            .Include(item => item.Venue)
            .SingleOrDefaultAsync(item => item.Id == request.InvitationId, cancellationToken);

        if (invitation is null
            || invitation.AcceptedAt is not null
            || invitation.ExpiresAt <= now
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(invitation.TokenHash), Encoding.UTF8.GetBytes(HashToken(request.Token))))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvitationInvalid);
        }

        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvitationInvalid);
        }

        // The invitation names an address; only that person may take it.
        if (Normalize(user.Email) != invitation.NormalizedEmail)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, VenueErrorCodes.InvitationForAnotherAddress);
        }

        // This endpoint has no venue in its route, so it never passes the venue policy: the freeze
        // has to be checked here too, or a suspended venue could still take on new members.
        if (VenueStatusRules.IsFrozen(invitation.Venue?.Status))
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, VenueErrorCodes.NotApproved);
        }

        if (await database.VenueMemberships.AnyAsync(
                member => member.VenueId == invitation.VenueId && member.UserId == user.Id, cancellationToken))
        {
            // Nothing left to accept, so the invitation stops being pending.
            invitation.AcceptedAt = now;
            await database.SaveChangesAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.AlreadyMember);
        }

        database.VenueMemberships.Add(new VenueMembership
        {
            VenueId = invitation.VenueId,
            UserId = user.Id,
            Role = VenueRole.Staff,
            Permissions = invitation.Permissions,
            CreatedAt = now,
        });
        invitation.AcceptedAt = now;

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DbErrors.IsUniqueViolation(exception))
        {
            // Two accepts raced (a double click, a retry); the membership already exists.
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.AlreadyMember);
        }

        return TypedResults.Ok(ToResponse(invitation.Venue!, VenueRole.Staff, invitation.Permissions));
    }

    private static async Task<Results<NoContent, ProblemHttpResult, NotFound>> ChangePermissionsAsync(
        Guid venueId,
        Guid userId,
        ChangePermissionsRequest request,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        if (!VenuePermissionSet.TryParse(request.Permissions, out var permissions))
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

        membership.Permissions = permissions;
        await database.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, ProblemHttpResult, NotFound>> RemoveMemberAsync(
        Guid venueId,
        Guid userId,
        AppDbContext database,
        CancellationToken cancellationToken)
    {
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

    private static string Normalize(string? email) => email?.Trim().ToUpperInvariant() ?? string.Empty;

    private static string GenerateToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static VenueResponse ToResponse(Venue venue, VenueRole role, VenuePermissions permissions) =>
        new(
            venue.Id,
            venue.Code,
            venue.Name,
            venue.AddressLine,
            venue.District,
            venue.Province,
            venue.Status.ToString(),
            role.ToString(),
            VenuePermissionSet.Describe(role == VenueRole.Owner ? VenuePermissions.All : permissions));

    private static VenueMemberResponse ToResponse(VenueMembership membership) =>
        new(
            membership.UserId,
            membership.User?.Email ?? string.Empty,
            membership.Role.ToString(),
            VenuePermissionSet.Describe(
                membership.Role == VenueRole.Owner ? VenuePermissions.All : membership.Permissions));

}
