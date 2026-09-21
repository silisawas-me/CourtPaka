using CourtBooking.Api.Identity;

namespace CourtBooking.Api.Bookings;

/// <summary>How a complaint reached the platform (PRD US-22). Stored, so the numbers must not move.</summary>
public enum ComplaintChannel
{
    Email = 1,
    Phone = 2,
    Line = 3,
    Other = 4,
}

public enum ComplaintStatus
{
    Open = 1,
    Resolved = 2,
}

/// <summary>
/// Somebody telling the platform a booking went wrong (PRD US-22). Always about one booking, so
/// the booking's own history is what an admin reads to find out what happened.
///
/// Opened once and resolved once, with what was done written down; nothing reopens it. A new
/// problem with the same booking is a new complaint, which keeps each answer beside its question.
/// </summary>
public sealed class Complaint
{
    public const int DetailsMaxLength = 2000;
    public const int ResolutionMaxLength = 2000;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid BookingId { get; init; }

    public required string Details { get; init; }

    public required ComplaintChannel Channel { get; init; }

    public ComplaintStatus Status { get; set; } = ComplaintStatus.Open;

    public required DateTimeOffset OpenedAt { get; init; }

    public required Guid OpenedByUserId { get; init; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public Guid? ResolvedByUserId { get; set; }

    /// <summary>What was done about it. Required to resolve (PRD US-22).</summary>
    public string? Resolution { get; set; }

    public Booking? Booking { get; init; }

    public AppUser? OpenedBy { get; init; }

    public AppUser? ResolvedBy { get; init; }
}

/// <summary>
/// One time an admin looked at a booker's slip through a complaint (PRD US-22, PRD 8).
///
/// A slip is somebody's bank account. The platform may see it only while a complaint about that
/// booking is open, and every look is written down before the file is handed over — so the
/// record exists even if the download is interrupted, and nobody can look without leaving one.
/// </summary>
public sealed class SlipViewing
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid ComplaintId { get; init; }

    public required Guid SlipId { get; init; }

    public required Guid ViewedByUserId { get; init; }

    public required DateTimeOffset ViewedAt { get; init; }

    public Complaint? Complaint { get; init; }

    public PaymentSlip? Slip { get; init; }

    public AppUser? ViewedBy { get; init; }
}
