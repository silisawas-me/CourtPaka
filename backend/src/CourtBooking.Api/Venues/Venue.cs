using CourtBooking.Api.Identity;

namespace CourtBooking.Api.Venues;

/// <summary>
/// A badminton venue on the platform. Everything a venue owns is scoped to it, and permissions are
/// granted per venue rather than globally, because one person can hold different roles at different
/// venues (PRD 2, US-14).
/// </summary>
public sealed class Venue
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Short code that prefixes the venue's document numbers (PRD 7.4).</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    public VenueStatus Status { get; set; } = VenueStatus.Pending;

    public required DateTimeOffset CreatedAt { get; init; }
}

public enum VenueStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Suspended = 4,
}

/// <summary>
/// What a person may do at one venue. Owners implicitly hold every permission, so the stored flags
/// only ever describe staff (PRD US-14).
/// </summary>
public sealed class VenueMembership
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    public required Guid UserId { get; init; }

    public required VenueRole Role { get; set; }

    public required VenuePermissions Permissions { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public Venue? Venue { get; init; }

    public AppUser? User { get; init; }

    public bool Allows(VenuePermissions permission) =>
        Role == VenueRole.Owner || Permissions.HasFlag(permission);
}

/// <summary>
/// An offer of staff access. It exists before the person does, because the invited address may not
/// have an account yet (PRD US-14).
/// </summary>
public sealed class VenueInvitation
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    public required string Email { get; init; }

    public required VenuePermissions Permissions { get; set; }

    /// <summary>Only the hash is stored; the token itself lives in the invitation email.</summary>
    public required string TokenHash { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public Venue? Venue { get; init; }
}

public enum VenueRole
{
    Owner = 1,
    Staff = 2,
}

[Flags]
public enum VenuePermissions
{
    None = 0,
    VerifySlip = 1 << 0,
    ManageBookings = 1 << 1,
    CloseCourt = 1 << 2,
    ViewReports = 1 << 3,
    ManageSettings = 1 << 4,

    /// <summary>What a newly invited staff member gets unless the owner changes it (PRD US-14).</summary>
    StaffDefault = VerifySlip | ManageBookings | CloseCourt,

    All = VerifySlip | ManageBookings | CloseCourt | ViewReports | ManageSettings,
}

public static class VenuePermissionSet
{
    /// <summary>The permissions an owner can actually grant; the composites above are shorthand.</summary>
    public static readonly VenuePermissions[] Grantable =
    [
        VenuePermissions.VerifySlip,
        VenuePermissions.ManageBookings,
        VenuePermissions.CloseCourt,
        VenuePermissions.ViewReports,
        VenuePermissions.ManageSettings,
    ];

    public static bool IsValid(VenuePermissions permissions) => (permissions & ~VenuePermissions.All) == 0;
}
