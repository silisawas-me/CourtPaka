namespace CourtBooking.Api.Venues;

public sealed record CreateVenueRequest(string Code, string Name);

public sealed record RenameVenueRequest(string Name);

public sealed record InviteMemberRequest(string Email, VenuePermissions? Permissions);

public sealed record ChangePermissionsRequest(VenuePermissions Permissions);

public sealed record VenueResponse(Guid Id, string Code, string Name, string Status);

public sealed record VenueMemberResponse(
    Guid UserId,
    string Email,
    string Role,
    string[] Permissions);

public static class VenueErrorCodes
{
    public const string CodeAlreadyUsed = "venue.code_already_used";
    public const string UnknownUser = "venue.unknown_user";
    public const string AlreadyMember = "venue.already_member";
    public const string OwnerCannotBeChanged = "venue.owner_cannot_be_changed";
    public const string InvalidPermissions = "venue.invalid_permissions";
}
