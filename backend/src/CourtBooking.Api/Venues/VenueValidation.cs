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
    public const int AddressLineMaxLength = 200;
    public const int DistrictMaxLength = 100;
    public const int ProvinceMaxLength = 100;

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

    /// <summary>
    /// Everything the venue has to say about itself before it can take money or issue a document
    /// (PRD US-10, 7.2). Each part answers under its own code so the form can point at the field.
    /// </summary>
    public static string? ValidateBusiness(VenueBusinessRequest business) =>
        ValidateText(
            business.PromptPayId, VenueBusiness.PromptPayIdMaxLength,
            VenueErrorCodes.InvalidPromptPayId)
        ?? ValidateText(
            business.PromptPayAccountName, VenueBusiness.AccountNameMaxLength,
            VenueErrorCodes.InvalidPromptPayAccountName)
        ?? ValidateText(
            business.LegalName, VenueBusiness.LegalNameMaxLength,
            VenueErrorCodes.InvalidLegalName)
        ?? ValidateTaxId(business.TaxId)
        ?? ValidateTaxBranch(business.TaxBranch)
        ?? ValidateText(
            business.BillingAddress, VenueBusiness.BillingAddressMaxLength,
            VenueErrorCodes.InvalidBillingAddress)
        ?? ValidateCoordinates(business.Latitude, business.Longitude);

    /// <summary>Thirteen digits and nothing else. The checksum is the Revenue Department's to judge.</summary>
    public static string? ValidateTaxId(string? taxId)
    {
        var trimmed = taxId?.Trim();
        return trimmed is null
               || trimmed.Length != VenueBusiness.TaxIdLength
               || !trimmed.All(char.IsAsciiDigit)
            ? VenueErrorCodes.InvalidTaxId
            : null;
    }

    public static string? ValidateTaxBranch(string? branch)
    {
        var trimmed = branch?.Trim();
        return trimmed is null
               || trimmed.Length != VenueBusiness.TaxBranchLength
               || !trimmed.All(char.IsAsciiDigit)
            ? VenueErrorCodes.InvalidTaxBranch
            : null;
    }

    /// <summary>
    /// Both or neither, and on the planet. A pin with only one half of itself is not a place.
    /// </summary>
    public static string? ValidateCoordinates(double? latitude, double? longitude) =>
        (latitude, longitude) switch
        {
            (null, null) => null,
            (not null, not null) when latitude is >= -90 and <= 90
                                      && longitude is >= -180 and <= 180 => null,
            _ => VenueErrorCodes.InvalidCoordinates,
        };

    /// <summary>The rule every free-text field shares: something, once trimmed, and not too long.</summary>
    public static string? ValidateText(string? value, int maxLength, string code)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength ? code : null;
    }

    /// <summary>
    /// Where the venue is, in the three parts a booker reads and searches by (PRD US-10, US-02).
    /// Each part refuses under its own code, so the form can point at the field that is wrong.
    /// </summary>
    public static string? ValidateAddress(string? line, string? district, string? province) =>
        ValidateText(line, AddressLineMaxLength, VenueErrorCodes.InvalidAddressLine)
        ?? ValidateText(district, DistrictMaxLength, VenueErrorCodes.InvalidDistrict)
        ?? ValidateText(province, ProvinceMaxLength, VenueErrorCodes.InvalidProvince);

    public static string? ValidateEmail(string? email)
    {
        var trimmed = email?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > EmailMaxLength || !EmailValidator.IsValid(trimmed)
            ? VenueErrorCodes.InvalidEmail
            : null;
    }
}
