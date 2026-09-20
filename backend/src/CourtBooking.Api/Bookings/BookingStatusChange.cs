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
    /// Why, where the state machine asks for a reason (PRD 6.1). The moves that exist so far need
    /// none, so this is null.
    ///
    /// US-13's reasons are a closed set that decides the refund (<c>CustomerRequest</c>,
    /// <c>VenueInitiated</c>, <c>PaymentNotReceived</c>) and US-12's rejection needs free text
    /// beside one: when those arrive this splits into a code and a note, and the transition table
    /// says which moves may not be made without one.
    /// </summary>
    public string? Reason { get; init; }

    public Booking? Booking { get; init; }

    public AppUser? ChangedBy { get; init; }
}
