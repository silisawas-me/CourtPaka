using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace CourtBooking.Api.Identity;

/// <summary>
/// Identity's own email rules — a valid address, and one account per address — for the accounts
/// that have an address. A LINE account may have none (PRD US-01), which Identity's built-in rule
/// (<c>RequireUniqueEmail</c>) cannot express: it demands an address of everybody.
///
/// The same error codes as Identity's, so registration reads them the way it always has. The
/// unique index on the normalized address stays the final word when two sign-ups race.
/// </summary>
public sealed class EmailRules(IdentityErrorDescriber describe) : IUserValidator<AppUser>
{
    private static readonly EmailAddressAttribute Address = new();

    public async Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user)
    {
        var email = await manager.GetEmailAsync(user);
        if (email is null)
        {
            return IdentityResult.Success;
        }

        if (string.IsNullOrWhiteSpace(email) || !Address.IsValid(email))
        {
            return IdentityResult.Failed(describe.InvalidEmail(email));
        }

        var owner = await manager.FindByEmailAsync(email);
        return owner is not null && owner.Id != user.Id
            ? IdentityResult.Failed(describe.DuplicateEmail(email))
            : IdentityResult.Success;
    }
}
