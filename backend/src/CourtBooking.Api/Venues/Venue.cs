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

    /// <summary>Street and building, as the venue writes it for a booker to read (PRD US-10).</summary>
    public required string AddressLine { get; set; }

    /// <summary>District (เขต/อำเภอ), which is how people look for a court near them.</summary>
    public required string District { get; set; }

    public required string Province { get; set; }

    public VenueStatus Status { get; set; } = VenueStatus.Pending;

    /// <summary>
    /// Where the money goes and who the venue is for tax (PRD US-10). Owned by the venue rather
    /// than kept beside it, because a venue without it cannot be paid and cannot issue a
    /// document — there is no useful state where it is absent.
    /// </summary>
    public required VenueBusiness Business { get; set; }

    /// <summary>
    /// Which version of the venue agreement was accepted, and when, and by whom (PRD US-10, Q8).
    /// It includes letting the platform issue documents in the venue's name, so what was agreed
    /// to has to be answerable later — the same reason a booker's consent is a row (PDPA).
    /// </summary>
    /// <remarks>
    /// Null on a venue that joined before there was an agreement to accept. Not a default and
    /// not a blank — nothing was accepted, and a column that said otherwise would be the one
    /// thing this record exists to be trusted about.
    /// </remarks>
    public string? AgreementVersion { get; set; }

    public DateTimeOffset? AgreementAcceptedAt { get; set; }

    public Guid? AgreementAcceptedByUserId { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public enum VenueStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Suspended = 4,
}

public static class VenueStatusRules
{
    /// <summary>
    /// A suspended or rejected venue is frozen: it can be read by its people, but nothing about it
    /// changes — including who belongs to it — until the platform lifts it (PRD US-20).
    /// </summary>
    public static bool IsFrozen(VenueStatus? status) => status is VenueStatus.Suspended or VenueStatus.Rejected;
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

    /// <summary>
    /// Whether this person wants an email each time a slip arrives here (PRD US-17). It is the
    /// one notice that can be turned off, because it is the only one that comes on an ordinary
    /// day rather than when something is stuck — and it is per venue rather than per person,
    /// because somebody working two counters may want to hear from one of them.
    /// </summary>
    public bool WantsSlipEmails { get; set; } = true;

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

    /// <summary>The address as the owner typed it; shown back to them.</summary>
    public required string Email { get; init; }

    /// <summary>
    /// Upper-cased address used for every comparison. Without it "Bob@x.com" and "bob@x.com" are two
    /// live invitations for one mailbox, and replacing one would leave the other usable.
    /// </summary>
    public required string NormalizedEmail { get; init; }

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

    /// <summary>
    /// Permissions travel as names in both directions, so a client never has to know the bit values
    /// and cannot grant the wrong right by getting them wrong.
    /// </summary>
    public static string[] Describe(VenuePermissions permissions) =>
        Grantable.Where(permission => permissions.HasFlag(permission))
            .Select(permission => permission.ToString())
            .ToArray();

    public static bool TryParse(IEnumerable<string>? names, out VenuePermissions permissions)
    {
        permissions = VenuePermissions.None;
        foreach (var name in names ?? [])
        {
            var match = Grantable.FirstOrDefault(
                permission => string.Equals(permission.ToString(), name, StringComparison.OrdinalIgnoreCase));
            if (match == VenuePermissions.None)
            {
                return false;
            }

            permissions |= match;
        }

        return true;
    }
}
