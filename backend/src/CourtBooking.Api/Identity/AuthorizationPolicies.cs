using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Identity;

public static class AuthorizationPolicies
{
    public const string EmailConfirmed = "EmailConfirmed";
}

public static class AppClaimTypes
{
    public const string EmailConfirmed = "email_confirmed";
    public const string Language = "language";
}

/// <summary>
/// Puts verification state and language into the sign-in cookie so endpoints do not hit the database for them.
/// Confirming an email changes the security stamp, so the cookie is refreshed on the next validation interval.
/// </summary>
public sealed class AppUserClaimsPrincipalFactory(
    UserManager<AppUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole<Guid>>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AppClaimTypes.EmailConfirmed, user.EmailConfirmed ? "true" : "false"));
        identity.AddClaim(new Claim(AppClaimTypes.Language, user.Language));
        return identity;
    }
}
