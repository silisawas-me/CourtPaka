using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
        user.SuspendedAt is null && user.DeletedAt is null && await base.CanSignInAsync(user);

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

/// <summary>
/// Whether an account may still act — not suspended (US-22), not deleted (PDPA, S-15) — asked of
/// the row itself, not of the cookie, which can outlive either for a revalidation interval.
///
/// Inside a transaction the row is share-locked, so it is also the queue with a deletion in
/// progress: that takes the row for update, so a write gated here either finishes first (and the
/// deletion then sees it) or waits and is refused.
/// </summary>
public static class AccountGate
{
    public const string Closed = "auth.account_closed";

    private sealed class Standing
    {
        public DateTimeOffset? SuspendedAt { get; init; }

        public DateTimeOffset? DeletedAt { get; init; }
    }

    /// <summary>Null when the account may act; otherwise the code to refuse with.</summary>
    public static async Task<string?> RefusalAsync(
        Data.AppDbContext database,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var rows = await database.Database
            .SqlQuery<Standing>(
                $"""
                SELECT "SuspendedAt", "DeletedAt" FROM "AspNetUsers"
                WHERE "Id" = {userId}
                FOR SHARE
                """)
            .ToListAsync(cancellationToken);

        return rows.SingleOrDefault() switch
        {
            null or { DeletedAt: not null } => Closed,
            { SuspendedAt: not null } => AuthErrorCodes.AccountSuspended,
            _ => null,
        };
    }
}
