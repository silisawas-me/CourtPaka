using CourtBooking.Api.Identity;

namespace CourtBooking.Api.Venues;

/// <summary>What happened to somebody's seat at a venue. Stored, so the numbers must not move.</summary>
public enum MembershipChangeKind
{
    /// <summary>They took a seat: the owner applying, or staff accepting an invitation.</summary>
    Joined = 1,

    /// <summary>The owner changed what a member of staff may do.</summary>
    PermissionsChanged = 2,

    /// <summary>The owner took the seat away.</summary>
    Removed = 3,

    /// <summary>They left, by asking to be forgotten (PDPA, S-15).</summary>
    Left = 4,
}

/// <summary>
/// One change to who may do what at a venue (PRD 8: "audit log สำหรับ ... สิทธิ์", US-14).
///
/// Rows are only ever added — the database refuses anything else — because the question this
/// answers comes up when something went wrong: who could confirm slips on the day a slip was
/// confirmed that should not have been, and who gave them that.
/// </summary>
public sealed class MembershipChange
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    /// <summary>Whose seat it is.</summary>
    public required Guid UserId { get; init; }

    public required MembershipChangeKind Kind { get; init; }

    public required VenueRole Role { get; init; }

    /// <summary>What they could do before; null when they had no seat.</summary>
    public VenuePermissions? PermissionsBefore { get; init; }

    /// <summary>What they can do after; null when they no longer have a seat.</summary>
    public VenuePermissions? PermissionsAfter { get; init; }

    /// <summary>Who made the change: the owner, or the person themselves when they joined or left.</summary>
    public required Guid ChangedByUserId { get; init; }

    public required DateTimeOffset ChangedAt { get; init; }

    public Venue? Venue { get; init; }

    public AppUser? User { get; init; }

    public AppUser? ChangedBy { get; init; }
}
