namespace CourtBooking.Api.Identity;

/// <summary>
/// What an account still needs before it can hold a court (PRD US-01). One answer, read by the
/// booking endpoint that enforces it and by /api/auth/me so the app can say it before a booker
/// picks hours they cannot take.
///
/// A proved address is enough. A LINE account, which may have no address, is enough once it has a
/// phone number: that is how the venue reaches the booker.
/// </summary>
public static class BookingEligibility
{
    /// <summary>The code of what is missing, or null when nothing is.</summary>
    public static string? MissingFor(bool emailConfirmed, bool signsInWithLine, string? phoneNumber) =>
        emailConfirmed ? null
        : !signsInWithLine ? AuthErrorCodes.EmailNotVerified
        : string.IsNullOrEmpty(phoneNumber) ? AuthErrorCodes.PhoneRequired
        : null;
}
