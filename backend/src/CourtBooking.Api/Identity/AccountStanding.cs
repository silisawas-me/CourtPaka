using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Identity;

/// <summary>
/// One time the platform suspended an account or let it back in (PRD US-22, PRD 8).
///
/// Rows are only ever added: who was stopped, by whom, when and why is what anybody asks when
/// the person disputes it, and the platform is the other party to that dispute.
/// </summary>
public sealed class AccountStatusChange
{
    public const int ReasonMaxLength = 500;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid UserId { get; init; }

    /// <summary>True for a suspension, false for letting the account back in.</summary>
    public required bool Suspended { get; init; }

    public required string Reason { get; init; }

    public required Guid ChangedByUserId { get; init; }

    public required DateTimeOffset ChangedAt { get; init; }

    public AppUser? User { get; init; }

    public AppUser? ChangedBy { get; init; }
}

/// <summary>
/// Sign-in that also asks whether the platform has suspended the account (PRD US-22).
///
/// Identity asks <see cref="CanSignInAsync"/> before it ever checks a password, so a suspended
/// account cannot start a session by any path — the password form, a refreshed cookie, or
/// anything added later that goes through the sign-in manager.
/// </summary>
public sealed class AppSignInManager(
    UserManager<AppUser> users,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<AppUser> claimsFactory,
    IOptions<IdentityOptions> options,
    ILogger<SignInManager<AppUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<AppUser> confirmation)
    : SignInManager<AppUser>(users, contextAccessor, claimsFactory, options, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(AppUser user) =>
        user.SuspendedAt is null && await base.CanSignInAsync(user);

    /// <summary>
    /// A live session is checked against the row every revalidation interval. The security stamp
    /// changes when an account is suspended, which is what ends a session already open; this
    /// makes the answer the same even for a cookie whose stamp somehow still matches.
    /// </summary>
    public override async Task<AppUser?> ValidateSecurityStampAsync(
        System.Security.Claims.ClaimsPrincipal? principal)
    {
        var user = await base.ValidateSecurityStampAsync(principal);
        return user is { SuspendedAt: null } ? user : null;
    }
}
