using System.ComponentModel.DataAnnotations;

namespace CourtBooking.Api.Venues;

/// <summary>
/// Request bodies arrive straight off the wire, so every field is checked before it reaches the
/// database: a malformed body must answer 400 with a code, never 500.
/// </summary>
public static class VenueValidation
{
    public const int CodeMaxLength = 6;
    public const int CodeMinLength = 3;
    public const int NameMaxLength = 200;
    public const int EmailMaxLength = 256;

    private static readonly EmailAddressAttribute EmailValidator = new();

    public static string? ValidateCode(string? code)
    {
        var trimmed = code?.Trim();
        return string.IsNullOrEmpty(trimmed)
               || trimmed.Length is < CodeMinLength or > CodeMaxLength
               || !trimmed.All(char.IsAsciiLetterOrDigit)
            ? VenueErrorCodes.InvalidCode
            : null;
    }

    public static string? ValidateName(string? name) =>
        ValidateText(name, NameMaxLength, VenueErrorCodes.InvalidName);

    /// <summary>The rule every free-text field shares: something, once trimmed, and not too long.</summary>
    public static string? ValidateText(string? value, int maxLength, string code)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength ? code : null;
    }

    public static string? ValidateEmail(string? email)
    {
        var trimmed = email?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > EmailMaxLength || !EmailValidator.IsValid(trimmed)
            ? VenueErrorCodes.InvalidEmail
            : null;
    }
}
