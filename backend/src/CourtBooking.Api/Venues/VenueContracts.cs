namespace CourtBooking.Api.Venues;

public sealed record CreateVenueRequest(string Code, string Name);

public sealed record RenameVenueRequest(string Name);

public sealed record InviteMemberRequest(string Email, VenuePermissions? Permissions);

public sealed record AcceptInvitationRequest(Guid InvitationId, string Token);

public sealed record ChangePermissionsRequest(VenuePermissions Permissions);

public sealed record VenueResponse(Guid Id, string Code, string Name, string Status);

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
    public const string InvalidEmail = "venue.invalid_email";
    public const string NotApproved = "venue.not_approved";
}
