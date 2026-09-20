using CourtBooking.Api.Identity;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// One move a booking made, and who made it (PRD 6.1: every status change is recorded with who,
/// when and why; PRD 8 lists booking status changes among the things that need an audit trail).
///
/// Rows are only ever added. A booking's history is what the venue, the platform and — when it
/// comes to a complaint (US-22) — an admin reads to find out what happened, so nothing rewrites it.
/// </summary>
public sealed class BookingStatusChange
{
    /// <summary>As much as a venue needs to explain itself, and no more.</summary>
    public const int ReasonMaxLength = 500;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// Two moves made in one transaction — confirming hours that were already played is both a
    /// confirm and a complete (PRD 6.1) — share an instant, and a v7 id is random inside a
    /// millisecond, so neither the clock nor the id puts them in order. What does is the chain:
    /// each row's <see cref="From"/> is the row before it's <see cref="To"/>. A reader drawing a
    /// booking's history follows that.
    /// </summary>

    public required Guid BookingId { get; init; }

    /// <summary>Null for the first row, which records the booking coming into existence.</summary>
    public required BookingStatus? From { get; init; }

    public required BookingStatus To { get; init; }

    public required DateTimeOffset ChangedAt { get; init; }

    /// <summary>
    /// Who moved it, or null when the system did — a hold running out belongs to nobody.
    /// </summary>
    public required Guid? ChangedByUserId { get; init; }

    /// <summary>
    /// Which of the closed set of reasons this move was made for, where one applies (PRD 6.1).
    /// It decides the refund, so it is also what anybody counting by reason has to count — the
    /// venue's own report of cancellations it caused (US-15), and the check that a court being
    /// closed left no booking behind (US-11). A column, not a prefix on <see cref="Reason"/>:
    /// <c>LIKE</c> is not an answer to "how many, and which".
    ///
    /// Null where the move needed no reason at all, and on the moves whose reason is free text
    /// rather than a choice — a slip being rejected says why in words.
    /// </summary>
    public CancellationReason? Cause { get; init; }

    /// <summary>
    /// What was written beside the reason, in the venue's own words (PRD 6.1). Free text, so
    /// nothing counts by it; it is there for whoever reads one booking's history — an admin
    /// looking at a complaint (US-22) — rather than for a report.
    /// </summary>
    public string? Reason { get; init; }

    public Booking? Booking { get; init; }

    public AppUser? ChangedBy { get; init; }
}
