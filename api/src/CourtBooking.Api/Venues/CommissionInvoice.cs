using CourtBooking.Api.Bookings;
using CourtBooking.Api.Identity;

namespace CourtBooking.Api.Venues;

/// <summary>Where a commission invoice stands (PRD US-21).</summary>
public enum CommissionInvoiceStatus
{
    /// <summary>Sent to the venue and waiting to be paid.</summary>
    Issued = 1,

    /// <summary>The venue says it has transferred, and has shown something for it.</summary>
    PaymentSubmitted = 2,

    /// <summary>The platform has seen the money.</summary>
    Paid = 3,
}

/// <summary>
/// One month's commission for one venue (PRD US-21, BR-08).
///
/// It is a document, so it carries a number that is never reused and never skipped (PRD 7.4),
/// and the lines it was worked out from are kept with it: a venue that asks why a month came to
/// that much is asking about particular bookings, and "trust the total" is not an answer.
///
/// The lines are also what stops a booking being charged twice. BR-08's third condition — not
/// already on any invoice — is read from them rather than from a flag on the booking, because a
/// flag can be true with no invoice to point at.
/// </summary>
public sealed class CommissionInvoice
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    /// <summary>The document number, as PRD 7.4 writes it: <c>PLT-INV-2027-000001</c>.</summary>
    public required string Number { get; init; }

    /// <summary>
    /// The month being billed, as its first day (PRD BR-10). The lines may reach back further —
    /// anything that settled late is carried forward into this month's invoice.
    /// </summary>
    public required DateOnly Month { get; init; }

    /// <summary>What the lines add up to. Kept, because the lines are what it was, not what it is.</summary>
    public required decimal AmountBaht { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>When payment is expected: the 16th, per PRD BR-09.</summary>
    public required DateOnly DueOn { get; init; }

    public CommissionInvoiceStatus Status { get; set; } = CommissionInvoiceStatus.Issued;

    /// <summary>When the venue said it had paid, and what it showed for it.</summary>
    public DateTimeOffset? SubmittedAt { get; set; }

    public string? EvidenceKey { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    public Guid? PaidByUserId { get; set; }

    /// <summary>Why the platform sent a claim of payment back, if it did.</summary>
    public string? RefusedReason { get; set; }

    public const int ReasonMaxLength = 400;

    public List<CommissionInvoiceLine> Lines { get; init; } = [];

    public Venue? Venue { get; init; }

    public AppUser? PaidBy { get; init; }
}

/// <summary>
/// One booking on one invoice, as it was charged (PRD US-21, BR-08). Everything here is a
/// snapshot: the rate could be changed tomorrow and the booking's money could be corrected, and
/// an invoice that has been sent has to keep saying what it said.
/// </summary>
public sealed class CommissionInvoiceLine
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid InvoiceId { get; init; }

    public required Guid BookingId { get; init; }

    /// <summary>The day it was played, which is what decides the month and the rate (PRD 6.2).</summary>
    public required DateOnly ServedOn { get; init; }

    /// <summary>What the venue kept out of it (PRD 6.2), which is the base the rate is taken of.</summary>
    public required decimal KeptBaht { get; init; }

    public required decimal Percent { get; init; }

    public required decimal AmountBaht { get; init; }

    public CommissionInvoice? Invoice { get; init; }

    public Booking? Booking { get; init; }
}

/// <summary>
/// Whether a booking's money is final (PRD 6.2, "ปิดยอดแล้ว"). One place, because it is what
/// decides whether a month may be billed for it — and a month billed for a booking still being
/// argued about is a month that has to be unpicked.
/// </summary>
public static class Settled
{
    /// <summary>
    /// How long a venue has to change what it recorded about an evening before the platform
    /// treats it as final. The same window the venue itself is given to correct a record
    /// (PRD 6.1, <see cref="VenueDecisions.CorrectionWindow"/>).
    /// </summary>
    public static readonly TimeSpan After = TimeSpan.FromHours(24);

    /// <summary>
    /// Whether this booking had settled by a given moment. Expired and rejected bookings settle
    /// the moment they end — there is nothing left to decide. A cancelled one settles once the
    /// venue has answered whether the money arrived, because until then what is owed is unknown.
    /// One that was played settles a day after its last hour, when the venue can no longer
    /// correct it.
    /// </summary>
    public static bool By(
        BookingStatus status,
        PaymentState payment,
        DateTimeOffset lastHourEndsAt,
        DateTimeOffset at) =>
        status switch
        {
            BookingStatus.Expired or BookingStatus.Rejected => true,
            BookingStatus.Cancelled => payment != PaymentState.Unconfirmed,
            BookingStatus.Completed or BookingStatus.NoShow => at >= lastHourEndsAt + After,
            _ => false,
        };
}
