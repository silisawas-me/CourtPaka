using System.Security.Claims;
using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CourtBooking.Api.Identity;

public sealed record DeleteAccountRequest(string? Password);

public static class AccountDeletionErrorCodes
{
    public const string WrongPassword = "account.wrong_password";
    public const string LockedOut = "account.locked_out";
    public const string OwnsAVenue = "account.owns_a_venue";
    public const string IsPlatformAdmin = "account.is_platform_admin";
    public const string HasUpcomingBookings = "account.has_upcoming_bookings";
    public const string HasMoneyPending = "account.has_money_pending";

    /// <summary>
    /// An account with no password (LINE) confirms by going to LINE and coming back, which stands
    /// in for typing the password again.
    /// </summary>
    public const string ConfirmWithLine = "account.confirm_with_line";
}

/// <summary>
/// A person asking to be forgotten (PDPA, PRD 8, S-15).
///
/// Nothing is deleted: the account is anonymised. Bookings, their history, refunds and the records
/// venues keep for tax stay, because they are the venue's records too — but none of them can be
/// traced back to the person any more once the address, phone and password are gone.
///
/// It is refused while somebody still depends on reaching the person: a venue they own, hours
/// they have not played yet or that a venue may still correct, or money that has not been settled
/// with them. Those are answered first, then the account can go.
///
/// The checks and the anonymising happen under a lock on the account row, which every write that
/// could make the person needed again also takes (<see cref="AccountGate"/>): a booking, a venue
/// or a staff seat is either made before the deletion looks — and then refuses it — or after, and
/// then is refused itself.
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
        HttpContext http,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        AppDbContext database,
        IDataProtectionProvider protection,
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

        // Asked again at the moment it matters: a borrowed session is not enough to erase a
        // person. An account with a password types it again — through the sign-in manager, so a
        // lockout stops the guessing here as it does at the sign-in page, and five wrong guesses
        // lock the account there too. An account without one (LINE) proves it at LINE instead,
        // within the last few minutes.
        if (user.PasswordHash is null)
        {
            if (!LineLoginEndpoints.RecentlyConfirmed(http, user.Id, protection))
            {
                return ApiProblem.Of(
                    StatusCodes.Status403Forbidden, AccountDeletionErrorCodes.ConfirmWithLine);
            }
        }
        else
        {
            var checkedPassword = await signIn.CheckPasswordSignInAsync(
                user, request.Password ?? "", lockoutOnFailure: true);
            if (checkedPassword.IsLockedOut)
            {
                return ApiProblem.Of(StatusCodes.Status403Forbidden, AccountDeletionErrorCodes.LockedOut);
            }

            if (!checkedPassword.Succeeded)
            {
                return ApiProblem.Of(StatusCodes.Status403Forbidden, AccountDeletionErrorCodes.WrongPassword);
            }
        }

        if (options.Value.PlatformAdmins.Any(admin =>
                string.Equals(admin.Trim(), user.Email, StringComparison.OrdinalIgnoreCase)))
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, AccountDeletionErrorCodes.IsPlatformAdmin);
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        // Held until the commit: anything that could make this person needed again waits here.
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {user.Id} FOR UPDATE""",
            cancellationToken);

        if (await RefusalAsync(database, user.Id, timeProvider.GetUtcNow(), cancellationToken)
            is { } refused)
        {
            return ApiProblem.Of(StatusCodes.Status409Conflict, refused);
        }

        var now = timeProvider.GetUtcNow();
        var formerAddress = user.NormalizedEmail;
        var placeholder = Placeholder(user.Id);

        // Staff seats go: a venue should not list somebody who no longer exists as a member.
        // Each is written down first, as the person leaving (PRD 8's audit of permissions).
        var seats = await database.VenueMemberships
            .AsNoTracking()
            .Where(member => member.UserId == user.Id)
            .ToListAsync(cancellationToken);
        database.MembershipChanges.AddRange(seats.Select(seat => new MembershipChange
        {
            VenueId = seat.VenueId,
            UserId = user.Id,
            Kind = MembershipChangeKind.Left,
            Role = seat.Role,
            PermissionsBefore = seat.Permissions,
            ChangedByUserId = user.Id,
            ChangedAt = now,
        }));
        await database.SaveChangesAsync(cancellationToken);

        await database.VenueMemberships
            .Where(member => member.UserId == user.Id)
            .ExecuteDeleteAsync(cancellationToken);

        // The LINE user id names the person as surely as the address does, and signing in with it
        // again must make a new account, not walk back into this one (PDPA, S-15).
        await database.UserLogins
            .Where(login => login.UserId == user.Id)
            .ExecuteDeleteAsync(cancellationToken);

        // An accepted invitation is the last copy of the address the platform holds. Pending ones
        // are left: they are the venue's own entry, and whoever owns the address may still sign up
        // again and take one.
        await database.VenueInvitations
            .Where(invitation => invitation.AcceptedAt != null && invitation.NormalizedEmail == formerAddress)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(invitation => invitation.Email, placeholder)
                    .SetProperty(invitation => invitation.NormalizedEmail, placeholder.ToUpperInvariant()),
                cancellationToken);

        user.Email = placeholder;
        user.UserName = placeholder;
        user.NormalizedEmail = placeholder.ToUpperInvariant();
        user.NormalizedUserName = placeholder.ToUpperInvariant();
        user.EmailConfirmed = false;
        user.PhoneNumber = null;
        user.DisplayName = null;
        user.PhoneNumberConfirmed = false;
        user.PasswordHash = null;
        user.DeletedAt = now;

        // A new stamp ends every other session this account had open, at its next revalidation;
        // until then AccountGate refuses anything it would write.
        var updated = await users.UpdateSecurityStampAsync(user);
        if (!updated.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not anonymise account {user.Id}: {string.Join(", ", updated.Errors.Select(e => e.Code))}");
        }

        await transaction.CommitAsync(cancellationToken);
        await signIn.SignOutAsync();
        LineLoginEndpoints.ForgetConfirmation(http, options.Value);

        AppEvents.For(loggers).LogInformation("account_deleted {UserId}", user.Id);

        return TypedResults.NoContent();
    }

    /// <summary>Why the account cannot go yet, or null if it can.</summary>
    private static async Task<string?> RefusalAsync(
        AppDbContext database,
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // A venue has exactly one owner, and it cannot be left with none (PRD US-14).
        if (await database.VenueMemberships.AnyAsync(
                member => member.UserId == userId && member.Role == VenueRole.Owner, cancellationToken))
        {
            return AccountDeletionErrorCodes.OwnsAVenue;
        }

        var mine = database.Bookings.Where(booking => booking.BookerUserId == userId);

        // Hours still to be played — and hours a venue may still correct, which can leave money
        // owed back after the play: a venue has CorrectionWindow after the last hour (US-13).
        var stillOpenFrom = now - VenueDecisions.CorrectionWindow;
        var upcoming = await mine
            .Where(booking =>
                (booking.Status == BookingStatus.Held && booking.HoldExpiresAt > now)
                || booking.Status == BookingStatus.PendingVerification
                || ((booking.Status == BookingStatus.Confirmed
                        || booking.Status == BookingStatus.Completed
                        || booking.Status == BookingStatus.NoShow)
                    && booking.Slots.Any(slot => slot.EndsAt > stillOpenFrom)))
            .AnyAsync(cancellationToken);
        if (upcoming)
        {
            return AccountDeletionErrorCodes.HasUpcomingBookings;
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

        return moneyPending ? AccountDeletionErrorCodes.HasMoneyPending : null;
    }
}
