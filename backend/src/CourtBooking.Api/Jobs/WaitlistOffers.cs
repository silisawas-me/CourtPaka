using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Identity;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Jobs;

/// <summary>
/// Offering hours that came back to whoever asked for them first (PRD US-27).
///
/// It is not called from the places hours are given up. There are five of those — a booker
/// cancels, a venue cancels, a slip is turned down, nobody turns up, a hold runs out — and a
/// queue every one of them has to know about is a queue that gets forgotten by the sixth. Like
/// <see cref="BookerMail"/>, this reads the world as it now is and acts on what it finds.
///
/// An offer is an ordinary hold: the same fifteen minutes BR-02 gives everybody, held by the
/// same write that stops two people taking one court, and paid for the same way. Nothing here
/// invents a second way to keep a court, because a second way is a way the constraint that stops
/// double booking cannot see.
/// </summary>
public sealed class WaitlistOffers(
    AppDbContext database,
    TimeProvider timeProvider,
    ILoggerFactory loggers)
{
    /// <summary>
    /// How far ahead a queue is worth working. Hours today are the counter's to sell across the
    /// desk; a queue is for the days somebody planned around.
    /// </summary>
    public static readonly int OfferWithinDays = Availability.BookableDaysAhead;

    /// <summary>
    /// Settles the offers that have ended, then makes what new ones the day allows. Answers how
    /// many were made, which is what the sweep writes down.
    /// </summary>
    public async Task<int> WorkAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await SettleAsync(now, cancellationToken);
        return await OfferAsync(now, cancellationToken);
    }

    /// <summary>
    /// What became of the offers already out. The booking says it: taken up if it moved on to be
    /// paid for, and over if it ran out or was let go — and the place in the queue ends either
    /// way, so the next sweep offers the hours to whoever is behind them (PRD US-27).
    /// </summary>
    private async Task SettleAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var outstanding = await database.WaitlistEntries
            .Where(entry => entry.State == WaitlistState.Offered && entry.OfferedBookingId != null)
            .Select(entry => new
            {
                entry.Id,
                BookingId = entry.OfferedBookingId!.Value,
                Status = database.Bookings
                    .Where(booking => booking.Id == entry.OfferedBookingId)
                    .Select(booking => (BookingStatus?)booking.Status)
                    .FirstOrDefault(),
                LapsedAt = database.Bookings
                    .Where(booking => booking.Id == entry.OfferedBookingId)
                    .Select(booking => booking.HoldExpiresAt)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        foreach (var offer in outstanding)
        {
            // Still waiting to be answered: the hold is running and its time is not up.
            if (offer.Status == BookingStatus.Held && offer.LapsedAt > now)
            {
                continue;
            }

            var taken = offer.Status is BookingStatus.PendingVerification
                or BookingStatus.Confirmed
                or BookingStatus.Completed;

            await database.WaitlistEntries
                .Where(entry => entry.Id == offer.Id && entry.State == WaitlistState.Offered)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(
                            entry => entry.State,
                            taken ? WaitlistState.Taken : WaitlistState.Gone)
                        .SetProperty(entry => entry.EndedAt, now),
                    cancellationToken);

            AppEvents.For(loggers).LogInformation(
                taken ? "waitlist_taken {EntryId} {BookingId}" : "waitlist_offer_lapsed {EntryId} {BookingId}",
                offer.Id,
                offer.BookingId);
        }
    }

    /// <summary>
    /// One offer per place in the queue, oldest ask first, for every day somebody is waiting on.
    /// A day is read once however many are waiting on it, because reading it is what costs.
    /// </summary>
    private async Task<int> OfferAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);

        var waiting = await database.WaitlistEntries
            .Where(entry =>
                entry.State == WaitlistState.Waiting
                && entry.Date >= today
                && entry.Date <= today.AddDays(OfferWithinDays))
            .OrderBy(entry => entry.Date)
            .ThenBy(entry => entry.AskedAt)
            .ToListAsync(cancellationToken);

        var offered = 0;

        foreach (var day in waiting.GroupBy(entry => (entry.VenueId, entry.Date)))
        {
            // A venue the platform has stopped is not selling, and a queue is a sale (PRD US-20).
            // What it asks for up front comes back with it: an offer is an ordinary hold, and an
            // ordinary hold is held on the venue's own terms (PRD US-28).
            var selling = await database.Venues
                .AsNoTracking()
                .Where(venue => venue.Id == day.Key.VenueId && venue.Status == VenueStatus.Approved)
                .Select(venue => new { venue.DepositPercent, venue.Risk })
                .SingleOrDefaultAsync(cancellationToken);

            if (selling is null)
            {
                continue;
            }

            var floor = await VenueDay.LoadAsync(
                database, day.Key.VenueId, day.Key.Date, now, cancellationToken);

            foreach (var entry in day)
            {
                if (await OfferOneAsync(
                        entry, floor, selling.DepositPercent, selling.Risk, now, cancellationToken))
                {
                    offered++;

                    // The hours this one took are gone; the day on hand no longer describes the
                    // floor, so whoever is behind them is offered on the next sweep rather than
                    // from a picture that is already out of date.
                    break;
                }
            }
        }

        return offered;
    }

    /// <summary>
    /// Puts hours aside for one place in the queue, if the day has a run that fits. Answers
    /// whether it did.
    /// </summary>
    private async Task<bool> OfferOneAsync(
        WaitlistEntry entry,
        VenueDay floor,
        int depositPercent,
        VenueRiskRule risk,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (Run(entry, floor, now) is not { } run)
        {
            return false;
        }

        // Everything a booking asks of an account, asked again here: the queue was joined a week
        // ago and an account can be closed, suspended or unverified since (PRD S-15, US-22).
        if (await AccountGate.RefusalAsync(database, entry.BookerUserId, cancellationToken)
            is not null)
        {
            return false;
        }

        // One hold at a time (PRD S-22). Somebody in the middle of paying for something else is
        // passed over rather than refused: their place stays, and the next sweep tries again.
        if (await BookedSlots.LiveHolds(database, now)
            .AnyAsync(held => held.BookerUserId == entry.BookerUserId, cancellationToken))
        {
            return false;
        }

        var priced = await BookingEndpoints.PriceSlotsAsync(
            database,
            entry.VenueId,
            [.. run.Select(hour => new BookingSlotRequest(hour.CourtId, entry.Date, hour.Hour))],
            now,
            loggers,
            cancellationToken);

        if (priced.Error is not null)
        {
            return false;
        }

        var policyId = await BookingEndpoints.InForcePolicyIdAsync(
            database, entry.VenueId, cancellationToken);
        // Offered hours are held on the same terms as hours somebody picked themselves, risk and
        // all: a queue is a way of asking, not a way round what the asking costs (PRD US-28).
        var misses = await DepositRisk.MissesAsync(
            database, entry.VenueId, entry.BookerUserId, risk.LookbackDays, now, cancellationToken);

        var (percent, reason) = DepositRisk.Asks(
            risk, depositPercent, misses, DepositRisk.TouchesPeak(risk, priced.Slots));

        var booking = Booking.Hold(
            entry.VenueId,
            entry.BookerUserId,
            policyId,
            priced.Slots,
            percent,
            reason,
            now);

        // The same write everybody else's booking goes through: the advisory lock, the exclusion
        // constraint, and a refusal if somebody took the hours in between (PRD BR-04).
        if (await BookingEndpoints.WriteNewAsync(database, booking, timeProvider, cancellationToken)
            is not null)
        {
            return false;
        }

        var claimed = await database.WaitlistEntries
            .Where(one => one.Id == entry.Id && one.State == WaitlistState.Waiting)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(one => one.State, WaitlistState.Offered)
                    .SetProperty(one => one.OfferedBookingId, booking.Id),
                cancellationToken);

        if (claimed == 0)
        {
            // They stood down while the hours were being put aside. Give them straight back
            // rather than leave a hold nobody asked for.
            await BookedSlots.ReleaseAsync(database, booking.Id, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            return false;
        }

        AppEvents.For(loggers).LogInformation(
            "waitlist_offered {EntryId} {BookingId} {VenueId} {Hours}",
            entry.Id,
            booking.Id,
            entry.VenueId,
            run.Count);

        return true;
    }

    /// <summary>
    /// The earliest run of free hours on one court that fits what was asked for, or null. One
    /// court, because a want of two hours is two hours of playing, not an hour here and an hour
    /// across the hall.
    /// </summary>
    private static IReadOnlyList<(Guid CourtId, int Hour)>? Run(
        WaitlistEntry entry,
        VenueDay floor,
        DateTimeOffset now)
    {
        for (var start = entry.FromHour; start + entry.Hours <= entry.UntilHour; start++)
        {
            foreach (var court in floor.Courts)
            {
                var hours = Enumerable.Range(start, entry.Hours).ToArray();

                var free = hours.All(hour =>
                    floor.Status(court.Id, hour) == HourStatus.Free
                    // An hour that has already begun is not one to offer somebody by email.
                    && PlatformRequirements.BangkokHour(entry.Date, hour) > now);

                if (free)
                {
                    return [.. hours.Select(hour => (court.Id, hour))];
                }
            }
        }

        return null;
    }
}
