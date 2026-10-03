using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>
/// The owner adds somebody to the venue's staff, and they can work at once (thai-fit T1, the
/// owner's decision of 2026-10-03, replacing the invitation link): a name, the phone they sign in
/// with, what they may do — and a six-digit passcode, shown to the owner once, to hand over at
/// the counter. The owner can set a new one whenever it is lost; the staff member can change it
/// themselves once in (<see cref="Passcodes"/>). Owner only, like every other change to the team.
/// </summary>
public static class StaffEndpoints
{
    public const int NameMaxLength = 100;

    public static void MapStaffEndpoints(this RouteGroupBuilder venue)
    {
        venue.MapPost("/staff", AddAsync).RequireAuthorization(VenuePolicies.OwnerOnly);
        venue.MapPost("/members/{userId:guid}/passcode", NewPasscodeAsync)
            .RequireAuthorization(VenuePolicies.OwnerOnly);
    }

    private static async Task<Results<Created<StaffPasscodeResponse>, ProblemHttpResult>> AddAsync(
        Guid venueId,
        AddStaffRequest request,
        CurrentVenue currentVenue,
        AppDbContext database,
        UserManager<AppUser> users,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > NameMaxLength)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.StaffNeedsName);
        }

        if (PhoneNumbers.Normalize(request.Phone) is not { } phone)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidPhone);
        }

        var permissions = VenuePermissions.StaffDefault;
        if (request.Permissions is not null && !VenuePermissionSet.TryParse(request.Permissions, out permissions))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidPermissions);
        }

        if (request.RefundLimitBaht is { } limit && !RefundLimits.IsALimit(limit))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, VenueErrorCodes.InvalidRefundLimit);
        }

        var ownerId = currentVenue.Require().UserId;
        var now = timeProvider.GetUtcNow();
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        // The phone signs in to one account. Somebody already on staff somewhere else keeps the
        // account and the passcode they have: an owner sets a passcode only for an account they
        // just made, never one somebody else's venue made, or one owner could take another's staff.
        var user = await users.Users.SingleOrDefaultAsync(
            one => one.UserName == AuthEndpoints.PhoneUserName(phone), cancellationToken);
        string? passcode = null;
        if (user is null)
        {
            user = new AppUser
            {
                UserName = AuthEndpoints.PhoneUserName(phone),
                PhoneNumber = phone,
                DisplayName = name,
                Language = SupportedLanguages.Default,
            };
            var made = await users.CreateAsync(user);
            if (!made.Succeeded)
            {
                return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.PhoneHasAnotherAccount);
            }

            passcode = Passcodes.New();
            await Passcodes.SetAsync(users, user, passcode);
        }
        else if (await database.VenueMemberships.AnyAsync(
                     member => member.VenueId == venueId && member.UserId == user.Id, cancellationToken))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.AlreadyMember);
        }
        else if (await AccountGate.RefusalAsync(database, user.Id, cancellationToken) is { } closed)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, closed);
        }

        var membership = new VenueMembership
        {
            VenueId = venueId,
            UserId = user.Id,
            User = user,
            Role = VenueRole.Staff,
            Permissions = permissions,
            RefundLimitBaht = request.RefundLimitBaht ?? 0m,
            CreatedAt = now,
        };
        database.VenueMemberships.Add(membership);
        // The seat is written down with who gave it, in the same save (PRD 8).
        database.MembershipChanges.Add(new MembershipChange
        {
            VenueId = venueId,
            UserId = user.Id,
            Kind = MembershipChangeKind.Joined,
            Role = VenueRole.Staff,
            PermissionsAfter = permissions,
            RefundLimitAfter = membership.RefundLimitBaht,
            ChangedByUserId = ownerId,
            ChangedAt = now,
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var neverSignedIn = !await database.UserConsents.AnyAsync(
            consent => consent.UserId == user.Id, cancellationToken);
        return TypedResults.Created(
            $"/api/venues/{venueId}/members",
            new StaffPasscodeResponse(VenueEndpoints.MemberResponse(membership, neverSignedIn), passcode));
    }

    /// <summary>
    /// A new passcode for a staff member who lost theirs. Only for an account that signs in with a
    /// passcode: an account with an address has a password nobody else may choose.
    /// </summary>
    private static async Task<Results<Ok<StaffPasscodeResponse>, ProblemHttpResult, NotFound>> NewPasscodeAsync(
        Guid venueId,
        Guid userId,
        AppDbContext database,
        UserManager<AppUser> users,
        CancellationToken cancellationToken)
    {
        var membership = await database.VenueMemberships
            .Include(member => member.User)
            .SingleOrDefaultAsync(member => member.VenueId == venueId && member.UserId == userId, cancellationToken);
        if (membership?.User is not { } user)
        {
            return TypedResults.NotFound();
        }

        if (membership.Role != VenueRole.Staff || !Passcodes.Is(user))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, VenueErrorCodes.NotAPasscodeAccount);
        }

        var passcode = Passcodes.New();
        await Passcodes.SetAsync(users, user, passcode);

        var neverSignedIn = !await database.UserConsents.AnyAsync(
            consent => consent.UserId == user.Id, cancellationToken);
        return TypedResults.Ok(new StaffPasscodeResponse(VenueEndpoints.MemberResponse(membership, neverSignedIn), passcode));
    }
}
