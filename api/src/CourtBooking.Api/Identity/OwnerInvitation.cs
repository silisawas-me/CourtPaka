using System.Security.Claims;
using CourtBooking.Api.Data;
using CourtBooking.Api.Email;
using CourtBooking.Api.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Identity;

/// <summary>
/// The platform asking somebody to bring their venue onto badPaka (docs/plan/owner-complete.md 3a).
/// With sign-up closed (<see cref="AppOptions.OpenSignUp"/> off) an address can only become an
/// account when somebody invited it: a venue its staff, or the platform its owner.
/// </summary>
/// <remarks>
/// No token: what it opens is the right to sign up with that address, and signing up already
/// proves the address by the verification email before the account can do anything that matters.
/// </remarks>
public sealed class OwnerInvitation
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The address as the admin typed it; shown back to them.</summary>
    public required string Email { get; init; }

    /// <summary>Upper-cased, for every comparison — as <see cref="Venues.VenueInvitation"/>.</summary>
    public required string NormalizedEmail { get; init; }

    public required string Language { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required Guid CreatedByUserId { get; init; }

    /// <summary>When the address signed up. An invitation used once is spent.</summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    public const int EmailMaxLength = 256;

    /// <summary>Two weeks: time to find the email and set up, short enough not to linger.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(14);
}

public sealed record InviteOwnerRequest(string? Email, string? Language);

public sealed record OwnerInvitationResponse(
    Guid Id,
    string Email,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt);

/// <summary>Who may still sign up while sign-up is closed, and the platform's door to invite owners.</summary>
public static class OwnerInvitations
{
    /// <summary>How many recent invitations the admin page lists.</summary>
    public const int Shown = 50;

    public static void MapOwnerInvitationEndpoints(this IEndpointRouteBuilder api)
    {
        var admin = api.MapGroup("/admin/owner-invitations").RequireAuthorization(PlatformAdmins.PolicyName);
        admin.MapGet("", ListAsync);
        admin.MapPost("", InviteAsync);
    }

    /// <summary>
    /// Whether this address may sign up with sign-up closed: a live invitation waits for it, from
    /// the platform (to own a venue) or from a venue (to work there).
    /// </summary>
    public static async Task<bool> InvitedAsync(
        AppDbContext database,
        string normalizedEmail,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await database.OwnerInvitations.AnyAsync(
            invitation => invitation.NormalizedEmail == normalizedEmail
                && invitation.AcceptedAt == null
                && invitation.ExpiresAt > now,
            cancellationToken)
        || await database.VenueInvitations.AnyAsync(
            invitation => invitation.NormalizedEmail == normalizedEmail
                && invitation.AcceptedAt == null
                && invitation.ExpiresAt > now,
            cancellationToken);

    /// <summary>An owner invitation is spent once its address has an account.</summary>
    public static Task SpendAsync(
        AppDbContext database,
        string normalizedEmail,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        database.OwnerInvitations
            .Where(invitation => invitation.NormalizedEmail == normalizedEmail && invitation.AcceptedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(invitation => invitation.AcceptedAt, now), cancellationToken);

    private static async Task<Ok<OwnerInvitationResponse[]>> ListAsync(
        AppDbContext database,
        CancellationToken cancellationToken)
    {
        var found = await database.OwnerInvitations
            .AsNoTracking()
            .OrderByDescending(invitation => invitation.CreatedAt)
            .Take(Shown)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(found.Select(Draw).ToArray());
    }

    private static async Task<Results<Created<OwnerInvitationResponse>, ProblemHttpResult>> InviteAsync(
        InviteOwnerRequest request,
        ClaimsPrincipal principal,
        AppDbContext database,
        UserManager<AppUser> users,
        ITransactionalEmailSender emails,
        IOptions<AppOptions> options,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim() ?? string.Empty;
        if (email.Length is 0 or > OwnerInvitation.EmailMaxLength || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.InvalidEmail);
        }

        var language = request.Language ?? SupportedLanguages.Default;
        if (!SupportedLanguages.IsSupported(language))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, AuthErrorCodes.UnsupportedLanguage);
        }

        var normalized = users.NormalizeEmail(email);
        if (await database.Users.AnyAsync(user => user.NormalizedEmail == normalized, cancellationToken))
        {
            // They have an account already: they apply for a venue from it, no invitation needed.
            return ApiProblem.Of(StatusCodes.Status409Conflict, AuthErrorCodes.AlreadyHasAccount);
        }

        var now = time.GetUtcNow();

        // One live invitation per address: a new one replaces what is waiting, as a venue's does.
        await database.OwnerInvitations
            .Where(invitation => invitation.NormalizedEmail == normalized && invitation.AcceptedAt == null)
            .ExecuteDeleteAsync(cancellationToken);

        var invitation = new OwnerInvitation
        {
            Email = email,
            NormalizedEmail = normalized,
            Language = language,
            ExpiresAt = now + OwnerInvitation.Lifetime,
            CreatedAt = now,
            CreatedByUserId = CallerId.Of(principal),
        };
        database.OwnerInvitations.Add(invitation);
        await database.SaveChangesAsync(cancellationToken);

        var link = $"{options.Value.BaseUrl.TrimEnd('/')}/register?email={Uri.EscapeDataString(email)}&as=owner";
        var (subject, body) = AccountLetters.OwnerInvitation(language, link);
        try
        {
            await emails.SendAsync(
                new EmailMessage(email, language, subject, body, AccountLetters.OwnerInvitationTemplate),
                CancellationToken.None);
        }
        catch (Exception)
        {
            // The invitation stands; the admin can send it again from the page.
        }

        return TypedResults.Created($"/api/admin/owner-invitations/{invitation.Id}", Draw(invitation));
    }

    private static OwnerInvitationResponse Draw(OwnerInvitation invitation) =>
        new(invitation.Id, invitation.Email, invitation.CreatedAt, invitation.ExpiresAt, invitation.AcceptedAt);
}
