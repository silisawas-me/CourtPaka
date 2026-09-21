using System.Security.Claims;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Identity;

public sealed record AdminUserResponse(
    Guid Id,
    string Email,
    bool EmailConfirmed,
    DateTimeOffset? SuspendedAt,
    bool IsPlatformAdmin);

public sealed record AccountStatusChangeResponse(
    bool Suspended,
    string Reason,
    string? ChangedByEmail,
    DateTimeOffset ChangedAt);

public sealed record AdminUserDetailResponse(
    AdminUserResponse User,
    AccountStatusChangeResponse[] History);

public sealed record AccountStandingRequest(string? Reason);

public static class AdminUserErrorCodes
{
    public const string ReasonRequired = "admin.reason_required";
    public const string ReasonTooLong = "admin.reason_too_long";
    public const string CannotSuspendAdmin = "admin.cannot_suspend_admin";
    public const string AlreadySuspended = "admin.already_suspended";
    public const string NotSuspended = "admin.not_suspended";
    public const string QueryTooShort = "admin.query_too_short";
}

/// <summary>
/// The platform finding people and stopping them (PRD US-22).
///
/// A suspended account cannot sign in and its open sessions end at the next revalidation, but
/// nothing it already booked is touched: a confirmed booking is the venue's business with a
/// customer, and a suspension is the platform's business with the account.
/// </summary>
public static class AdminUserEndpoints
{
    /// <summary>
    /// Search is by address, and at least this much of one: the list is for finding somebody,
    /// not for reading the member list page by page (PDPA, PRD 8).
    /// </summary>
    public const int MinQueryLength = 3;

    public const int MaxResults = 50;

    public static void MapAdminUserEndpoints(this IEndpointRouteBuilder routes)
    {
        var admin = routes.MapGroup("/admin/users")
            .WithTags("Platform")
            .RequireAuthorization(PlatformAdmins.PolicyName);

        admin.MapGet("/", SearchAsync);
        admin.MapGet("/{userId:guid}", OneAsync);
        admin.MapPost("/{userId:guid}/suspend", SuspendAsync);
        admin.MapPost("/{userId:guid}/reinstate", ReinstateAsync);
    }

    private static async Task<Results<Ok<AdminUserResponse[]>, ProblemHttpResult>> SearchAsync(
        string? q,
        AppDbContext database,
        IOptions<AppOptions> options,
        CancellationToken cancellationToken)
    {
        var query = q?.Trim() ?? "";
        if (query.Length < MinQueryLength)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AdminUserErrorCodes.QueryTooShort);
        }

        // Addresses are stored normalised upper-case, which the unique index already covers.
        var normalized = query.ToUpperInvariant();
        var pattern = "%" + PublicVenueEndpoints.Like(normalized) + "%";

        var users = await database.Users
            .AsNoTracking()
            // The same escaping as the venue search, with the escape character named rather than
            // assumed: without it an address with "_" in it stops being findable by it.
            // A forgotten account is not somebody to find: its placeholder names nobody (S-15).
            .Where(user => user.DeletedAt == null)
            .Where(user => EF.Functions.Like(
                user.NormalizedEmail!, pattern, PublicVenueEndpoints.LikeEscape))
            .OrderBy(user => user.NormalizedEmail)
            .Take(MaxResults)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(users.Select(user => Drawn(user, options.Value)).ToArray());
    }

    private static async Task<Results<Ok<AdminUserDetailResponse>, NotFound>> OneAsync(
        Guid userId,
        AppDbContext database,
        IOptions<AppOptions> options,
        CancellationToken cancellationToken)
    {
        var user = await database.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(one => one.Id == userId, cancellationToken);
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(await DetailAsync(database, user, options.Value, cancellationToken));
    }

    private static Task<Results<Ok<AdminUserDetailResponse>, ProblemHttpResult, NotFound>> SuspendAsync(
        Guid userId,
        AccountStandingRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        AppDbContext database,
        IOptions<AppOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        MoveAsync(
            suspend: true, userId, request, principal, users, database, options, timeProvider,
            loggers, cancellationToken);

    private static Task<Results<Ok<AdminUserDetailResponse>, ProblemHttpResult, NotFound>> ReinstateAsync(
        Guid userId,
        AccountStandingRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        AppDbContext database,
        IOptions<AppOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken) =>
        MoveAsync(
            suspend: false, userId, request, principal, users, database, options, timeProvider,
            loggers, cancellationToken);

    /// <summary>
    /// Suspends or reinstates, with a reason either way: both are decisions about a person, and
    /// both are asked about later (PRD 8). The flag and the history row are written together.
    /// </summary>
    private static async Task<Results<Ok<AdminUserDetailResponse>, ProblemHttpResult, NotFound>> MoveAsync(
        bool suspend,
        Guid userId,
        AccountStandingRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        AppDbContext database,
        IOptions<AppOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AdminUserErrorCodes.ReasonRequired);
        }

        if (reason.Length > AccountStatusChange.ReasonMaxLength)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AdminUserErrorCodes.ReasonTooLong);
        }

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        // Being an admin is configuration, not something the app hands out or takes away; an
        // admin stopping another would be an argument the configuration has to settle (US-20).
        // Checked against the addresses the configuration names, whether or not confirmed yet.
        if (suspend && options.Value.PlatformAdmins.Any(admin =>
                string.Equals(admin.Trim(), user.Email, StringComparison.OrdinalIgnoreCase)))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, AdminUserErrorCodes.CannotSuspendAdmin);
        }

        if (suspend == user.SuspendedAt is not null)
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict,
                suspend ? AdminUserErrorCodes.AlreadySuspended : AdminUserErrorCodes.NotSuspended);
        }

        var now = timeProvider.GetUtcNow();
        var byUserId = Guid.Parse(users.GetUserId(principal)!);

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        user.SuspendedAt = suspend ? now : null;

        // A new stamp is what ends the sessions already open: each is checked against it at the
        // next revalidation and signed out when it no longer matches.
        var updated = await users.UpdateSecurityStampAsync(user);

        // Somebody else moved the account between the read and this write; their answer stands.
        if (updated.Errors.Any(error => error.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure)))
        {
            return ApiProblem.Of(
                StatusCodes.Status409Conflict,
                suspend ? AdminUserErrorCodes.AlreadySuspended : AdminUserErrorCodes.NotSuspended);
        }

        if (!updated.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not update account {userId}: {string.Join(", ", updated.Errors.Select(e => e.Code))}");
        }

        database.AccountStatusChanges.Add(new AccountStatusChange
        {
            UserId = user.Id,
            Suspended = suspend,
            Reason = reason,
            ChangedByUserId = byUserId,
            ChangedAt = now,
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            (suspend ? "user_suspended" : "user_reinstated") + " {UserId} {By}", user.Id, byUserId);

        return TypedResults.Ok(await DetailAsync(database, user, options.Value, cancellationToken));
    }

    private static async Task<AdminUserDetailResponse> DetailAsync(
        AppDbContext database,
        AppUser user,
        AppOptions options,
        CancellationToken cancellationToken)
    {
        var history = await database.AccountStatusChanges
            .AsNoTracking()
            .Where(change => change.UserId == user.Id)
            .OrderByDescending(change => change.ChangedAt)
            .Select(change => new AccountStatusChangeResponse(
                change.Suspended, change.Reason, change.ChangedBy!.Email, change.ChangedAt))
            .ToArrayAsync(cancellationToken);

        return new AdminUserDetailResponse(Drawn(user, options), history);
    }

    private static AdminUserResponse Drawn(AppUser user, AppOptions options) =>
        new(
            user.Id,
            user.Email!,
            user.EmailConfirmed,
            user.SuspendedAt,
            PlatformAdmins.Includes(user, options));
}
