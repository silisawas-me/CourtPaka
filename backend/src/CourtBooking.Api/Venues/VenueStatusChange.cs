using CourtBooking.Api.Identity;

namespace CourtBooking.Api.Venues;

/// <summary>
/// One move a venue's standing made, and who made it (PRD US-20, PRD 8).
///
/// Rows are only ever added. Whether a venue was suspended, when, and what it was told is the
/// first thing anybody asks when a venue disputes it — and the platform is the other party to
/// that dispute, so the record has to outlive anybody's memory of it.
/// </summary>
public sealed class VenueStatusChange
{
    /// <summary>As much as the platform needs to explain itself to a venue.</summary>
    public const int ReasonMaxLength = 500;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    /// <summary>Null on the first row, which records the venue applying.</summary>
    public required VenueStatus? From { get; init; }

    public required VenueStatus To { get; init; }

    public required DateTimeOffset ChangedAt { get; init; }

    /// <summary>
    /// The admin who decided, or null where the venue itself did — asking to be looked at again
    /// after a refusal is a move the venue makes (PRD US-10).
    /// </summary>
    public required Guid? ChangedByUserId { get; init; }

    /// <summary>
    /// Why. Required for the moves that go against the venue — a refusal or a suspension — and
    /// it is what the venue is told, so it is written for them to read rather than for us.
    /// </summary>
    public string? Reason { get; init; }

    public Venue? Venue { get; init; }

    public AppUser? ChangedBy { get; init; }
}

/// <summary>
/// Which moves a venue's standing may make (PRD US-10, US-20). Small enough to read, and kept
/// beside the row it guards so the two cannot drift.
/// </summary>
public static class VenueStatusTransitions
{
    private static readonly HashSet<(VenueStatus From, VenueStatus To)> Allowed =
    [
        (VenueStatus.Pending, VenueStatus.Approved),
        (VenueStatus.Pending, VenueStatus.Rejected),

        // A venue that was turned away, having put itself right, asks again (PRD US-10).
        (VenueStatus.Rejected, VenueStatus.Pending),

        (VenueStatus.Approved, VenueStatus.Suspended),
        (VenueStatus.Suspended, VenueStatus.Approved),
    ];

    /// <summary>
    /// The moves that may not be made without saying why: the ones a venue would want explained.
    /// </summary>
    private static readonly HashSet<(VenueStatus From, VenueStatus To)> NeedReason =
    [
        (VenueStatus.Pending, VenueStatus.Rejected),
        (VenueStatus.Approved, VenueStatus.Suspended),
    ];

    public static bool CanMove(VenueStatus from, VenueStatus to) => Allowed.Contains((from, to));

    public static bool MustSayWhy(VenueStatus from, VenueStatus to) =>
        NeedReason.Contains((from, to));
}
