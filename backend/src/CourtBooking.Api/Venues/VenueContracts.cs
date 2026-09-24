namespace CourtBooking.Api.Venues;

/// <summary>
/// Where the money goes and who the venue is for tax (PRD US-10). Coordinates are optional; a
/// booker finds a court by district and province, and a pin nobody placed beats a wrong one.
/// </summary>
public sealed record VenueBusinessRequest(
    string PromptPayId,
    string PromptPayAccountName,
    bool IsVatRegistered,
    string LegalName,
    string TaxId,
    string TaxBranch,
    string BillingAddress,
    double? Latitude = null,
    double? Longitude = null);

/// <summary>
/// Applying to join the platform (PRD US-10). The agreement version is sent back by the applicant
/// rather than assumed, the same way a booker's consent is: what was on screen is what was
/// agreed to, and a version that has moved on since must not be recorded as accepted.
/// </summary>
public sealed record CreateVenueRequest(
    string Code,
    string Name,
    string AddressLine,
    string District,
    string Province,
    VenueBusinessRequest Business,
    string AgreementVersion);

/// <summary>
/// The venue's name and where it is — what a booker reads (PRD US-02, US-11).
///
/// Deliberately not the business block. This is behind <c>ManageSettings</c>, which an owner may
/// delegate, and where the money lands is not delegable (PRD US-14): it has its own endpoint and
/// its own policy. Leaving a field here that nothing reads is how it gets wired up by accident.
/// </summary>
public sealed record UpdateVenueRequest(
    string Name,
    string AddressLine,
    string District,
    string Province);

/// <summary>What the venue agreed to, and what the platform is asking for now (PRD US-10, Q8).</summary>
public sealed record VenueAgreementResponse(string Version);

/// <summary>The venue's own details, for the screens that edit them (PRD US-10).</summary>
public sealed record VenueBusinessResponse(
    string PromptPayId,
    string PromptPayAccountName,
    bool IsVatRegistered,
    string LegalName,
    string TaxId,
    string TaxBranch,
    string BillingAddress,
    double? Latitude,
    double? Longitude);

public sealed record InviteMemberRequest(string Email, string[]? Permissions);

public sealed record AcceptInvitationRequest(Guid InvitationId, string Token);

public sealed record ChangePermissionsRequest(string[] Permissions);

/// <summary>How long this venue waits before a booking counts as a no-show (PRD US-24).</summary>
public sealed record GraceRequest(int Minutes);

/// <summary>
/// How much of a booking's price this venue asks for before it holds the hours (PRD US-28).
/// </summary>
public sealed record DepositRequest(int Percent);

/// <summary>
/// A venue always arrives with what the caller may do there, so screens and guards never have to
/// work it out from the member list (PRD US-14).
/// </summary>
public sealed record VenueResponse(
    Guid Id,
    string Code,
    string Name,
    string AddressLine,
    string District,
    string Province,
    string Status,
    string Role,
    string[] Permissions,
    /// <summary>
    /// Whether this member wants to hear each time a slip arrives here (PRD US-17). It belongs to
    /// the reader rather than to the venue, which is why it travels with role and permissions.
    /// </summary>
    bool WantsSlipEmails,
    /// <summary>
    /// How much of a booking's price this venue asks for up front, as a percentage (PRD US-28).
    /// A hundred is the whole of it, which is what every venue asks for until it says otherwise.
    /// </summary>
    int DepositPercent);

public sealed record VenueMemberResponse(Guid UserId, string Email, string Role, string[] Permissions);

/// <summary>
/// What is waiting at this venue for the person asking (PRD US-17). Each number is counted only
/// for somebody who could do something about it, so a member who checks slips is not shown the
/// money and a member who handles money is not shown the queue.
/// </summary>
public sealed record VenueAttentionResponse(int SlipsToCheck, int BookingsWithMoneyWaiting);

/// <summary>What this member wants to hear about, for the one notice that can be turned off.</summary>
public sealed record NotificationPreferenceRequest(bool WantsSlipEmails);

public sealed record VenueInvitationResponse(Guid Id, string Email, string[] Permissions, DateTimeOffset ExpiresAt);

public static class VenueErrorCodes
{
    /// <summary>
    /// The wait before somebody counts as not having come has to be a wait a person could keep:
    /// nothing, up to an hour (PRD US-24).
    /// </summary>
    public const string InvalidGrace = "venue.invalid_grace";
    public const string InvalidDeposit = "venue.invalid_deposit";

    public const string CodeAlreadyUsed = "venue.code_already_used";

    /// <summary>
    /// A venue is answered by email — the platform's decision, the slip queue, the money
    /// (US-10, US-17, US-20) — so an account with no address cannot run one (PRD US-01).
    /// </summary>
    public const string OwnerNeedsEmail = "venue.owner_needs_email";
    public const string AlreadyMember = "venue.already_member";
    public const string OwnerCannotBeChanged = "venue.owner_cannot_be_changed";
    public const string InvalidPermissions = "venue.invalid_permissions";
    public const string InvitationInvalid = "venue.invitation_invalid";
    public const string InvitationForAnotherAddress = "venue.invitation_for_another_address";
    public const string InvalidCode = "venue.invalid_code";
    public const string InvalidName = "venue.invalid_name";
    public const string InvalidAddressLine = "venue.invalid_address_line";
    public const string InvalidDistrict = "venue.invalid_district";
    public const string InvalidProvince = "venue.invalid_province";
    public const string InvalidEmail = "venue.invalid_email";
    public const string NotApproved = "venue.not_approved";
    public const string NotFound = "venue.not_found";

    public const string InvalidPromptPayId = "venue.invalid_promptpay_id";
    public const string InvalidPromptPayAccountName = "venue.invalid_promptpay_account_name";
    public const string InvalidLegalName = "venue.invalid_legal_name";
    public const string InvalidTaxId = "venue.invalid_tax_id";
    public const string InvalidTaxBranch = "venue.invalid_tax_branch";
    public const string InvalidBillingAddress = "venue.invalid_billing_address";
    public const string InvalidCoordinates = "venue.invalid_coordinates";

    /// <summary>
    /// The agreement the applicant accepted is not the one being asked for. They see the current
    /// one and accept that instead; nothing is recorded in the meantime (PRD US-10, Q8).
    /// </summary>
    public const string AgreementOutOfDate = "venue.agreement_out_of_date";

    /// <summary>Only a venue the platform turned away has anything to ask again about.</summary>
    public const string NotRefused = "venue.not_refused";

    public const string InvalidStatus = "venue.invalid_status";

    /// <summary>The venue is not where that decision could be made from (PRD US-20).</summary>
    public const string StatusCannotMoveThere = "venue.status_cannot_move_there";

    public const string ReasonRequired = "venue.reason_required";
    public const string ReasonTooLong = "venue.reason_too_long";
}

/// <summary>A decision the platform makes about a venue (PRD US-20).</summary>
public sealed record VenueDecisionRequest(string? Reason);

/// <summary>A venue as the platform's own screen lists it (PRD US-20).</summary>
public sealed record AdminVenueResponse(
    Guid Id,
    string Code,
    string Name,
    string AddressLine,
    string District,
    string Province,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>One move a venue's standing made, as the platform reads its history (PRD US-20).</summary>
public sealed record VenueStatusChangeResponse(
    string? From,
    string To,
    DateTimeOffset ChangedAt,
    string? Reason);

/// <summary>
/// Everything needed to judge one application (PRD US-20): what the venue said about itself,
/// what it agreed to, and everything the platform has already decided about it.
/// </summary>
public sealed record AdminVenueDetailResponse(
    AdminVenueResponse Venue,
    VenueBusinessResponse Business,
    string? AgreementVersion,
    DateTimeOffset? AgreementAcceptedAt,
    VenueStatusChangeResponse[] History);

/// <summary>What a booker sees about a venue before signing in (PRD US-02).</summary>
/// <summary>
/// A venue as someone with no account sees it. Deliberately not the venue's code: that prefixes
/// every document number (PRD 7.4), and a booker has no use for it.
/// </summary>
public sealed record PublicVenueResponse(
    Guid Id,
    string Name,
    string AddressLine,
    string District,
    string Province);

/// <summary>One hour of one court: whether it can be taken, and what it costs.</summary>
public sealed record HourResponse(int Hour, string Status, decimal? BahtPerHour);

public sealed record CourtAvailabilityResponse(Guid CourtId, string Name, HourResponse[] Hours);

/// <summary>
/// One day at one venue, and the venue itself, so the page that draws the grid needs one request.
/// The opening hours are named as well as the cells, so it can say "closed today" rather than
/// drawing an empty grid. <see cref="LastBookableDate"/> is the server's booking window, so the
/// picker offers exactly the days the server will accept.
/// </summary>
public sealed record AvailabilityResponse(
    PublicVenueResponse Venue,
    DateOnly Date,
    DateOnly LastBookableDate,
    int? OpensHour,
    int? ClosesHour,
    CourtAvailabilityResponse[] Courts);

public static class AvailabilityErrorCodes
{
    public const string DateInThePast = "availability.date_in_the_past";
    public const string DateTooFarAhead = "availability.date_too_far_ahead";
}
