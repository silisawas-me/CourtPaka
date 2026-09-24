using CourtBooking.Api.Identity;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Bookings;

/// <summary>Where a booking came from. Staff bookings arrive with US-13.</summary>
public enum BookingChannel
{
    Online = 1,
    Staff = 2,
}

/// <summary>
/// How somebody paid at the counter (PRD US-13). The money is already in the venue's hands by the
/// time the booking is written, which is why a counter booking starts confirmed.
/// </summary>
public enum CounterPayment
{
    Cash = 1,
    Transfer = 2,
}

/// <summary>
/// Whether the venue has the money (PRD 6.2). It starts as not received and only a person with
/// the venue's authority says otherwise — which is what decides whether anything is owed back.
/// </summary>
public enum PaymentState
{
    NotReceived = 1,
    Received = 2,

    /// <summary>
    /// The booker walked away from a slip the venue had not checked yet. Nobody knows whether the
    /// money arrived until the venue says (PRD 6.2, US-13).
    /// </summary>
    Unconfirmed = 3,
}

/// <summary>
/// The booking's place in the state machine of PRD 6.1. Only the first two exist so far: paying
/// for a hold is US-04, and everything a venue does to a booking is US-12 and US-13.
/// </summary>
public enum BookingStatus
{
    Held = 1,
    PendingVerification = 2,
    Confirmed = 3,
    Completed = 4,
    Cancelled = 5,
    Expired = 6,
    Rejected = 7,
    NoShow = 8,
}

/// <summary>
/// One person's claim on some court-hours at one venue. It is created <see cref="BookingStatus.Held"/>
/// and holds its slots for fifteen minutes while it is paid for (PRD BR-02); what it costs and what
/// happens if it is cancelled are copied in at creation and never re-read, so a venue changing its
/// settings cannot change a booking already made (PRD BR-05).
/// </summary>
public sealed class Booking
{
    /// <summary>How long a booking holds its slots before the payment window closes (PRD BR-02).</summary>
    public static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(15);

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    /// <summary>
    /// The account that booked, for a booking made online. Null for one taken at the counter: the
    /// customer standing there does not need an account, and the PRD says so (US-13). The two
    /// shapes are held apart by a check constraint, so neither can be written half-filled.
    /// </summary>
    public Guid? BookerUserId { get; init; }

    public required BookingChannel Channel { get; init; }

    /// <summary>Who the counter booked for, in the words the customer gave (PRD US-13).</summary>
    public string? CustomerName { get; init; }

    /// <summary>Optional, and only ever used by the venue to reach the customer (PRD US-13).</summary>
    public string? CustomerPhone { get; init; }

    /// <summary>How a counter booking was paid for; null for one made online.</summary>
    public CounterPayment? PaidAtCounter { get; init; }

    public const int CustomerNameMaxLength = 200;
    public const int CustomerPhoneMaxLength = 20;

    public BookingStatus Status { get; set; } = BookingStatus.Held;

    /// <summary>
    /// When the venue was reminded that this slip was still waiting with the hours about to be
    /// played (PRD US-17, S-23). Set before the message goes out, so it is a claim on the job
    /// rather than a record of it: a reminder that arrives twice is one people stop reading.
    /// </summary>
    public DateTimeOffset? SlipReminderSentAt { get; set; }

    /// <summary>Whether the venue has the money for this booking (PRD 6.2).</summary>
    public PaymentState PaymentState { get; set; } = PaymentState.NotReceived;

    /// <summary>
    /// Whether the person is coming, and then whether they came (PRD US-24). Apart from the
    /// status on purpose: a booking that is paid for says nothing about somebody walking in.
    /// </summary>
    public BookingArrival Arrival { get; set; } = BookingArrival.Unconfirmed;

    /// <summary>When they were checked in at the desk, or null.</summary>
    public DateTimeOffset? ArrivedAt { get; set; }

    /// <summary>
    /// What the venue owes the booker back, in baht (PRD 6.2). Zero unless the money arrived and
    /// the booking then ended in a way that gives some of it back; recomputed whenever the status
    /// or the payment state moves, never read from the policy after the fact (BR-05).
    /// </summary>
    public decimal RefundDueBaht { get; set; }

    /// <summary>
    /// The share of the booking, as a percentage, that its ending gives back (PRD 6.1). It is
    /// written down when the booking ends rather than worked out again later: the cancellation
    /// terms are a snapshot (BR-05) and the amount depends on how much notice was given, so a
    /// venue that says weeks afterwards that the money did arrive (US-13) must be able to arrive
    /// at the number the booker was shown, not at today's.
    /// </summary>
    public int RefundPercent { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the hold lapses. Read on every availability query, not only by the job (PRD 9.2).</summary>
    public required DateTimeOffset HoldExpiresAt { get; init; }

    /// <summary>The sum of the slots, in baht, as they were priced at creation (PRD BR-05).</summary>
    public required decimal TotalBaht { get; init; }

    /// <summary>
    /// What had to arrive before these hours were held, in baht (PRD US-28). A snapshot like the
    /// price beside it: the venue can change what it asks for tomorrow, and a booking made today
    /// was held on today's terms (BR-05). Equal to <see cref="TotalBaht"/> unless the venue asks
    /// for less, which is what makes the rest of the price something the desk collects.
    /// </summary>
    public required decimal DepositBaht { get; init; }

    /// <summary>The cancellation policy this booking is refunded under, copied in (PRD BR-05).</summary>
    public required Guid CancellationPolicyId { get; init; }

    public List<BookingSlot> Slots { get; init; } = [];

    /// <summary>Every move this booking has made, oldest first (PRD 6.1).</summary>
    public List<BookingStatusChange> StatusChanges { get; init; } = [];

    public Venue? Venue { get; init; }

    public AppUser? Booker { get; init; }

    public CancellationPolicy? CancellationPolicy { get; init; }

    /// <summary>
    /// A held booking with its slots. The slots are what the database refuses to double-book, so
    /// there is no way to write a booking without them (PRD BR-04).
    /// </summary>
    public static Booking Hold(
        Guid venueId,
        Guid bookerUserId,
        Guid cancellationPolicyId,
        IEnumerable<SlotPrice> slots,
        int depositPercent,
        DateTimeOffset at)
    {
        var total = slots.Sum(slot => slot.BahtPerHour);
        var booking = new Booking
        {
            VenueId = venueId,
            BookerUserId = bookerUserId,
            Channel = BookingChannel.Online,
            CreatedAt = at,
            HoldExpiresAt = at + HoldFor,
            CancellationPolicyId = cancellationPolicyId,
            TotalBaht = total,
            DepositBaht = Deposit.Of(total, depositPercent),
        };

        booking.Slots.AddRange(slots.Select(slot => new BookingSlot
        {
            BookingId = booking.Id,
            CourtId = slot.CourtId,
            StartsAt = slot.StartsAt,
            EndsAt = slot.StartsAt.AddHours(1),
            BahtPerHour = slot.BahtPerHour,
            IsActive = true,
        }));

        // The booking coming into existence is the first thing its history records.
        booking.StatusChanges.Add(
            BookingTransitions.Created(booking.Id, booking.Status, bookerUserId, at));

        return booking;
    }

    /// <summary>
    /// A booking taken at the counter for somebody standing there (PRD US-13). It starts
    /// confirmed with the money received, because both have already happened: there is no hold
    /// to wait on and no slip to check. The staff member who took it is on the first row of its
    /// history, which is where "who" lives for every other move a booking makes.
    ///
    /// It holds no account. The customer does not need one, and the channel is recorded so the
    /// commission rules can tell the two apart later (PRD S-13).
    /// </summary>
    public static Booking AtCounter(
        Guid venueId,
        string customerName,
        string? customerPhone,
        CounterPayment paid,
        Guid cancellationPolicyId,
        IEnumerable<SlotPrice> slots,
        Guid takenByUserId,
        DateTimeOffset at)
    {
        var booking = new Booking
        {
            VenueId = venueId,
            Channel = BookingChannel.Staff,
            CustomerName = customerName,
            CustomerPhone = customerPhone,
            PaidAtCounter = paid,
            Status = BookingStatus.Confirmed,
            PaymentState = PaymentState.Received,
            CreatedAt = at,

            // Nothing is held, so nothing lapses. Set to the moment it was made rather than left
            // in the future, so no reader that forgets to check the status can mistake it for a
            // live hold.
            HoldExpiresAt = at,
            CancellationPolicyId = cancellationPolicyId,
            TotalBaht = slots.Sum(slot => slot.BahtPerHour),

            // Paid where it was made, so there was never a part of it to wait for.
            DepositBaht = slots.Sum(slot => slot.BahtPerHour),
        };

        booking.Slots.AddRange(slots.Select(slot => new BookingSlot
        {
            BookingId = booking.Id,
            CourtId = slot.CourtId,
            StartsAt = slot.StartsAt,
            EndsAt = slot.StartsAt.AddHours(1),
            BahtPerHour = slot.BahtPerHour,
            IsActive = true,
        }));

        booking.StatusChanges.Add(
            BookingTransitions.Created(booking.Id, booking.Status, takenByUserId, at));

        return booking;
    }
}

/// <summary>One court-hour as it was priced when the booking was made.</summary>
public readonly record struct SlotPrice(Guid CourtId, DateTimeOffset StartsAt, decimal BahtPerHour);

/// <summary>
/// One court-hour a booking holds. <see cref="IsActive"/> is what the database's exclusion
/// constraint reads: exactly one active row may cover a court and an instant, so two people
/// confirming the same hour at the same moment cannot both succeed (PRD BR-04, 9.2).
/// </summary>
public sealed class BookingSlot
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid BookingId { get; init; }

    public required Guid CourtId { get; init; }

    public required DateTimeOffset StartsAt { get; init; }

    public required DateTimeOffset EndsAt { get; init; }

    public required decimal BahtPerHour { get; init; }

    /// <summary>Whether this slot still occupies the court. See PRD BR-04 for which statuses do.</summary>
    public required bool IsActive { get; set; }

    public Booking? Booking { get; init; }

    public Court? Court { get; init; }
}
