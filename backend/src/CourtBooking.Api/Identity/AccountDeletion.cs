using System.Security.Claims;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Identity;

public sealed record DeleteAccountRequest(string? Password);

public static class AccountDeletionErrorCodes
{
    public const string WrongPassword = "account.wrong_password";
    public const string OwnsAVenue = "account.owns_a_venue";
    public const string IsPlatformAdmin = "account.is_platform_admin";
    public const string HasUpcomingBookings = "account.has_upcoming_bookings";
    public const string HasMoneyPending = "account.has_money_pending";
}

/// <summary>
/// A person asking to be forgotten (PDPA, PRD 8, S-15).
///
/// Nothing is deleted: the account is anonymised. Bookings, their history, refunds and the records
/// venues keep for tax stay, because they are the venue's records too — but none of them can be
/// traced back to the person any more once the address, phone and password are gone.
///
/// It is refused while somebody still depends on reaching the person: a venue they own, hours
/// they have not played yet, or money that has not been settled with them. Those are answered
/// first, then the account can go.
///
/// Open question, recorded in CLAUDE.md: whether the slips they sent (a bank account, and the
/// venue's evidence of payment) are erased as well. Kept for now; Q8 is where that is decided.
/// </summary>
public static class AccountDeletion
{
    /// <summary>The address an anonymised account is left with: unique, and undeliverable.</summary>
    public static string Placeholder(Guid userId) => $"deleted-{userId:N}@deleted.invalid";

    public static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        DeleteAccountRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        AppDbContext database,
        IOptions<AppOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return ApiProblem.Of(StatusCodes.Status401Unauthorized, AuthErrorCodes.InvalidCredentials);
        }

        // Asked again at the moment it matters: a borrowed session is not enough to erase a person.
        if (string.IsNullOrEmpty(request.Password) || !await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            return ApiProblem.Of(StatusCodes.Status403Forbidden, AccountDeletionErrorCodes.WrongPassword);
        }

        if (options.Value.PlatformAdmins.Any(admin =>
                string.Equals(admin.Trim(), user.Email, StringComparison.OrdinalIgnoreCase)))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, AccountDeletionErrorCodes.IsPlatformAdmin);
        }

        // A venue has exactly one owner, and it cannot be left with none (PRD US-14).
        if (await database.VenueMemberships.AnyAsync(
                member => member.UserId == user.Id && member.Role == VenueRole.Owner, cancellationToken))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, AccountDeletionErrorCodes.OwnsAVenue);
        }

        var now = timeProvider.GetUtcNow();
        var mine = database.Bookings.Where(booking => booking.BookerUserId == user.Id);

        // Hours still to be played: the venue expects this person, and may need to reach them.
        var upcoming = await mine
            .Where(booking =>
                (booking.Status == BookingStatus.Held && booking.HoldExpiresAt > now)
                || booking.Status == BookingStatus.PendingVerification
                || (booking.Status == BookingStatus.Confirmed
                    && booking.Slots.Any(slot => slot.EndsAt > now)))
            .AnyAsync(cancellationToken);
        if (upcoming)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, AccountDeletionErrorCodes.HasUpcomingBookings);
        }

        // Money not yet settled: owed back to them, or waiting on the venue to say if it arrived.
        // Once the address is gone, nobody can tell them either way (PRD 6.2).
        var moneyPending = await mine
            .Where(booking =>
                booking.PaymentState == PaymentState.Unconfirmed
                || (booking.RefundDueBaht > 0
                    && booking.RefundDueBaht > database.RefundRecords
                        .Where(record => record.BookingId == booking.Id && record.VoidedAt == null)
                        .Sum(record => record.AmountBaht)))
            .AnyAsync(cancellationToken);
        if (moneyPending)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, AccountDeletionErrorCodes.HasMoneyPending);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        // Staff seats go: a venue should not list somebody who no longer exists as a member.
        await database.VenueMemberships
            .Where(member => member.UserId == user.Id)
            .ExecuteDeleteAsync(cancellationToken);

        var placeholder = Placeholder(user.Id);
        user.Email = placeholder;
        user.UserName = placeholder;
        user.NormalizedEmail = placeholder.ToUpperInvariant();
        user.NormalizedUserName = placeholder.ToUpperInvariant();
        user.EmailConfirmed = false;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;
        user.PasswordHash = null;
        user.DeletedAt = now;

        // A new stamp ends every other session this account had open.
        var updated = await users.UpdateSecurityStampAsync(user);
        if (!updated.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not anonymise account {user.Id}: {string.Join(", ", updated.Errors.Select(e => e.Code))}");
        }

        await transaction.CommitAsync(cancellationToken);
        await signIn.SignOutAsync();

        AppEvents.For(loggers).LogInformation("account_deleted {UserId}", user.Id);

        return TypedResults.NoContent();
    }
}
