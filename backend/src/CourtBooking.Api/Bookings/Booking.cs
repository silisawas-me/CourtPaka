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

    public required Guid BookerUserId { get; init; }

    public required BookingChannel Channel { get; init; }

    public BookingStatus Status { get; set; } = BookingStatus.Held;

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the hold lapses. Read on every availability query, not only by the job (PRD 9.2).</summary>
    public required DateTimeOffset HoldExpiresAt { get; init; }

    /// <summary>The sum of the slots, in baht, as they were priced at creation (PRD BR-05).</summary>
    public required decimal TotalBaht { get; init; }

    /// <summary>The cancellation policy this booking is refunded under, copied in (PRD BR-05).</summary>
    public required Guid CancellationPolicyId { get; init; }

    public List<BookingSlot> Slots { get; init; } = [];

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
        DateTimeOffset at)
    {
        var booking = new Booking
        {
            VenueId = venueId,
            BookerUserId = bookerUserId,
            Channel = BookingChannel.Online,
            CreatedAt = at,
            HoldExpiresAt = at + HoldFor,
            CancellationPolicyId = cancellationPolicyId,
            TotalBaht = slots.Sum(slot => slot.BahtPerHour),
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
