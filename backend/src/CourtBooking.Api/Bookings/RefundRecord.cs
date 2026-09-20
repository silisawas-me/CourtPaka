using CourtBooking.Api.Identity;

namespace CourtBooking.Api.Bookings;

/// <summary>How the venue got the money back to the booker (PRD US-18).</summary>
public enum RefundMethod
{
    Transfer = 1,
    Cash = 2,
}

/// <summary>
/// One transfer the venue made to a booker, written down after the fact (PRD US-18, BR-06).
///
/// The money moves outside this system — a bank app, a till — so this is a record of something
/// that already happened rather than an instruction to make it happen. It is written once and
/// never edited: a record that can be changed is not evidence of anything. The only thing that
/// may happen to one afterwards is being voided by the owner, with a reason, which puts the
/// amount back on what the venue still owes.
/// </summary>
public sealed class RefundRecord
{
    /// <summary>As much as a venue needs to explain itself, and no more.</summary>
    public const int NoteMaxLength = 500;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid BookingId { get; init; }

    public required decimal AmountBaht { get; init; }

    /// <summary>The day the money actually left, which need not be the day this was typed.</summary>
    public required DateOnly RefundedOn { get; init; }

    public required RefundMethod Method { get; init; }

    public string? Note { get; init; }

    public required Guid RecordedByUserId { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    /// <summary>
    /// When this record was taken back, if it was. A voided record stays in the table — what the
    /// venue said at the time is part of the history — and stops counting towards what has been
    /// sent back (PRD 6.2).
    /// </summary>
    public DateTimeOffset? VoidedAt { get; set; }

    public Guid? VoidedByUserId { get; set; }

    /// <summary>Why it was taken back. The owner may not do it without saying (PRD US-18).</summary>
    public string? VoidReason { get; set; }

    public Booking? Booking { get; init; }

    public AppUser? RecordedBy { get; init; }

    public bool Stands => VoidedAt is null;
}
