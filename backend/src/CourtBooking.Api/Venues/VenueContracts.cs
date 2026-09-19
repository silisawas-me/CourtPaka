namespace CourtBooking.Api.Venues;

public sealed record CreateVenueRequest(
    string Code,
    string Name,
    string AddressLine,
    string District,
    string Province);

/// <summary>The venue's public details: what it is called and where a booker will find it.</summary>
public sealed record UpdateVenueRequest(
    string Name,
    string AddressLine,
    string District,
    string Province);

public sealed record InviteMemberRequest(string Email, string[]? Permissions);

public sealed record AcceptInvitationRequest(Guid InvitationId, string Token);

public sealed record ChangePermissionsRequest(string[] Permissions);

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
    string[] Permissions);

public sealed record VenueMemberResponse(Guid UserId, string Email, string Role, string[] Permissions);

public sealed record VenueInvitationResponse(Guid Id, string Email, string[] Permissions, DateTimeOffset ExpiresAt);

public static class VenueErrorCodes
{
    public const string CodeAlreadyUsed = "venue.code_already_used";
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
}

/// <summary>What a booker sees about a venue before signing in (PRD US-02).</summary>
public sealed record PublicVenueResponse(
    Guid Id,
    string Code,
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
