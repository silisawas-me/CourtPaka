namespace CourtBooking.Api.Identity;

/// <summary>A Thai phone number, the way a venue would dial it back (PRD US-01).</summary>
public static class PhoneNumbers
{
    /// <summary>
    /// The number as ten (or nine, for a landline) digits starting with 0, or null when it is not
    /// one. Spaces and dashes are how people type them and are dropped; +66 is the same number.
    /// </summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null)
        {
            return null;
        }

        var digits = string.Concat(typed.Where(character => character is not (' ' or '-')));
        if (digits.StartsWith("+66", StringComparison.Ordinal))
        {
            digits = "0" + digits[3..];
        }

        return digits.Length is 9 or 10 && digits[0] == '0' && digits.All(char.IsAsciiDigit)
            ? digits
            : null;
    }
}
