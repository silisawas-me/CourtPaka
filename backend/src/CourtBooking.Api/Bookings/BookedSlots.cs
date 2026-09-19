using CourtBooking.Api.Data;
using CourtBooking.Api.Localization;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// Which court-hours are spoken for. One rule, read by the grid a booker looks at and by the write
/// that takes an hour, so the two can never disagree about what "taken" means.
/// </summary>
public static class BookedSlots
{
    /// <summary>
    /// The statuses that still occupy a court (PRD BR-04). <c>NoShow</c> is deliberately absent:
    /// the hours a no-show did not turn up for go back on sale.
    /// </summary>
    public static readonly BookingStatus[] Occupying =
    [
        BookingStatus.Held,
        BookingStatus.PendingVerification,
        BookingStatus.Confirmed,
        BookingStatus.Completed,
    ];

    /// <summary>
    /// A held booking past its expiry does not hold anything, whether or not the job that marks it
    /// <c>Expired</c> has run (PRD 9.2). Every read and every write goes through this.
    /// </summary>
    public static IQueryable<BookingSlot> Active(AppDbContext database, DateTimeOffset now) =>
        database.BookingSlots
            .Where(slot =>
                slot.IsActive
                && Occupying.Contains(slot.Booking!.Status)
                && (slot.Booking.Status != BookingStatus.Held || slot.Booking.HoldExpiresAt > now));

    /// <summary>The court-hours of one Bangkok day that a booker cannot take.</summary>
    public static async Task<HashSet<(Guid CourtId, int Hour)>> OnAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly date,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var from = PlatformRequirements.BangkokHour(date, 0);
        var until = PlatformRequirements.BangkokHour(date.AddDays(1), 0);

        var slots = await Active(database, now)
            .Where(slot =>
                slot.Booking!.VenueId == venueId && slot.StartsAt >= from && slot.StartsAt < until)
            .Select(slot => new { slot.CourtId, slot.StartsAt })
            .ToListAsync(cancellationToken);

        return slots
            .Select(slot => (slot.CourtId, PlatformRequirements.BangkokDateAndHour(slot.StartsAt).Hour))
            .ToHashSet();
    }
}
