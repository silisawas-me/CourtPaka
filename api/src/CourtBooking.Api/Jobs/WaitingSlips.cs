using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Jobs;

/// <summary>
/// Slips still waiting to be looked at when the hours they paid for are about to be played
/// (PRD US-17, S-23).
///
/// The rule is on its own because the awkward part is not finding them, it is telling the venue
/// exactly once. The row is claimed first and the message is sent afterwards: two instances, or
/// two ticks of one, then find nothing left to claim. The other order — send, then mark — sends
/// twice whenever the mark fails, and a reminder that arrives twice is a reminder people stop
/// reading.
/// </summary>
public static class WaitingSlips
{
    /// <summary>How long before the first hour the venue is reminded (PRD US-17, S-23).</summary>
    public static readonly TimeSpan RemindWithin = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Claims and answers every booking that needs the reminder now. Answers venue and booking,
    /// which is all the notifier needs, and nothing about the booker.
    /// </summary>
    public static async Task<IReadOnlyList<(Guid VenueId, Guid BookingId)>> AboutToBePlayedAsync(
        AppDbContext database,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var deadline = now + RemindWithin;

        // Still waiting, starting within the half hour, and not started yet — a booking already
        // being played is past reminding about, and the venue has a person standing in front of
        // it instead.
        var due = database.Bookings
            .Where(booking =>
                booking.Status == BookingStatus.PendingVerification
                && booking.SlipReminderSentAt == null
                && booking.Slots.Any(slot => slot.StartsAt > now && slot.StartsAt <= deadline));

        var claimed = await due
            .ExecuteUpdateAsync(
                set => set.SetProperty(booking => booking.SlipReminderSentAt, now),
                cancellationToken);

        if (claimed == 0)
        {
            return [];
        }

        // Read back by the mark rather than by the rule: the rule has just stopped matching them.
        return await database.Bookings
            .AsNoTracking()
            .Where(booking => booking.SlipReminderSentAt == now)
            .Select(booking => new ValueTuple<Guid, Guid>(booking.VenueId, booking.Id))
            .ToListAsync(cancellationToken);
    }
}
