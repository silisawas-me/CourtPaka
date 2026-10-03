using System.Security.Cryptography;
using CourtBooking.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Identity;

/// <summary>
/// Staff added by an owner sign in with their phone number and a six-digit passcode (thai-fit
/// T1, owner's decision 2026-10-03): the owner adds them and they can work at once, change the
/// passcode themselves once in, and the owner can set a new one whenever it is lost.
///
/// A passcode account is one with a phone user name (<see cref="AuthEndpoints.PhoneUserName"/>)
/// and no address. Only those can have their passcode set by an owner: an account with an address
/// is somebody's own, and nobody else may choose its password. Six digits are guessable without a
/// limit, which is why sign-in locks after five wrong tries (Program.cs).
/// </summary>
public static class Passcodes
{
    public const int Length = 6;

    /// <summary>Whether this account signs in with a passcode an owner can set.</summary>
    public static bool Is(AppUser user) =>
        user.Email is null && user.UserName?.StartsWith("tel-", StringComparison.Ordinal) == true;

    /// <summary>Six digits from a cryptographic source, leading zeros kept.</summary>
    public static string New() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static bool IsValid(string? code) =>
        code is { Length: Length } && code.All(char.IsAsciiDigit);

    /// <summary>
    /// Sets the passcode, bypassing the password rules (eight characters) that six digits would
    /// fail. A new security stamp ends every session the account had: a passcode set by somebody
    /// else, or changed because it leaked, must not leave the old sessions open.
    /// </summary>
    public static async Task SetAsync(UserManager<AppUser> users, AppUser user, string code)
    {
        user.PasswordHash = users.PasswordHasher.HashPassword(user, code);
        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not set the passcode: {string.Join(", ", updated.Errors.Select(error => error.Code))}");
        }

        await users.UpdateSecurityStampAsync(user);
        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
    }

    /// <summary>
    /// Whether the account still has to accept the privacy policy in force: a passcode account was
    /// made by an owner, so it has accepted nothing until its person signs in and says so (PDPA).
    /// </summary>
    public static async Task<bool> NeedsConsentAsync(
        AppDbContext database,
        AppUser user,
        string version,
        CancellationToken cancellationToken) =>
        Is(user)
        && !await database.UserConsents.AnyAsync(
            consent => consent.UserId == user.Id
                       && consent.Type == ConsentType.PrivacyPolicy
                       && consent.Version == version,
            cancellationToken);
}
