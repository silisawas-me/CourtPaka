namespace CourtBooking.Api.Venues;

/// <summary>
/// What a venue has to tell the platform about itself before it can take money (PRD US-10).
///
/// Three different things travel together here, and separating them is the point:
/// <list type="bullet">
///   <item>where the court actually is, which a booker uses to get there;</item>
///   <item>where the money goes, which US-04 puts in a QR;</item>
///   <item>who the venue is to the Revenue Department, which US-07 and US-16 print on documents
///     in the venue's own name.</item>
/// </list>
///
/// They are on the venue rather than in their own tables because a venue has exactly one of each
/// and they change by being corrected, not by being versioned. The documents that quote them keep
/// their own copy at the time they are issued (PRD 7).
/// </summary>
public sealed class VenueBusiness
{
    public const int PromptPayIdMaxLength = 20;
    public const int AccountNameMaxLength = 200;
    public const int LegalNameMaxLength = 200;
    public const int BillingAddressMaxLength = 400;

    /// <summary>A Thai tax number is thirteen digits, and a branch code is five (PRD 7.2).</summary>
    public const int TaxIdLength = 13;

    public const int TaxBranchLength = 5;

    /// <summary>The head office, which is what most venues are (PRD 7.2).</summary>
    public const string HeadOfficeBranch = "00000";

    /// <summary>
    /// Where the court is on a map. Optional: a venue can be found by district and province
    /// (US-02), and a pin nobody placed is better than a pin in the wrong place.
    /// </summary>
    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    /// <summary>
    /// The account a booker transfers to, and the name that should appear on the transfer so
    /// they can tell they are paying the right people (PRD US-04).
    /// </summary>
    public required string PromptPayId { get; set; }

    public required string PromptPayAccountName { get; set; }

    /// <summary>
    /// Whether the venue charges VAT, which decides which document it issues and whether a
    /// booker may ask for a full tax invoice at all (PRD 7.1, US-07).
    /// </summary>
    public required bool IsVatRegistered { get; set; }

    /// <summary>The name the venue trades under for tax, which is not always the name on the sign.</summary>
    public required string LegalName { get; set; }

    public required string TaxId { get; set; }

    public required string TaxBranch { get; set; }

    /// <summary>
    /// The address that goes on documents, which is often the accountant's or the head office's
    /// rather than the court's (PRD 7.2).
    /// </summary>
    public required string BillingAddress { get; set; }
}
