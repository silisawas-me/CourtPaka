using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Jobs;

/// <summary>
/// Filling in the weeks of every standing arrangement (PRD US-30).
///
/// The arrangement says "every Tuesday at seven"; this makes the Tuesdays, as far ahead as anybody
/// else can book (PRD S-04) and no further. That is what keeps the promise of BR-05 honest: a week
/// is priced and gets its cancellation terms on the day it is made, so a group agreed in March pays
/// April's evening rate in April rather than March's.
///
/// Each week is an ordinary counter booking, written by the same
/// <see cref="BookingEndpoints.WriteNewAsync"/> as everything else — the same advisory locks, the
/// same exclusion constraint. An arrangement never takes an hour somebody already has: where a week
/// refuses, the refusal is written down as a <see cref="SeriesMiss"/> for the venue to deal with,
/// which is the same answer closing a court gives when a booking is in the way (PRD US-11).
/// </summary>
public sealed class SeriesBookings(
    AppDbContext database,
    TimeProvider timeProvider,
    ILoggerFactory loggers)
{
    /// <summary>Makes whatever weeks are now inside the window. Answers how many it made.</summary>
    public async Task<int> WorkAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = PlatformRequirements.BangkokToday(timeProvider);
        var until = today.AddDays(Series.FillWithinDays);

        var running = await database.BookingSeries
            .AsNoTracking()
            .Where(one => one.State == SeriesState.Running
                && (one.UntilOn == null || one.UntilOn >= today))
            .OrderBy(one => one.CreatedAt)
            .ToListAsync(cancellationToken);

        // A venue the platform has stopped is not selling, and a week of a standing arrangement
        // is a sale (PRD US-20). Asked once for the whole sweep rather than once per venue: the
        // sweep runs every minute for as long as the platform is up, and the answer is a column.
        var selling = await database.Venues
            .AsNoTracking()
            .Where(one =>
                one.Status == VenueStatus.Approved
                && running.Select(series => series.VenueId).Contains(one.Id))
            .Select(one => one.Id)
            .ToListAsync(cancellationToken);

        var open = selling.ToHashSet();
        var made = 0;

        // The arrangement keeps its place at a venue that is stopped; it starts filling in again
        // if the venue comes back.
        foreach (var venue in running.Where(one => open.Contains(one.VenueId))
                     .GroupBy(one => one.VenueId))
        {
            // One venue's trouble is its own. A venue with no cancellation policy is a broken
            // invariant and says so by throwing — but it must not take the rest of the platform's
            // weeks with it, and the next sweep is the recovery (Caretaker).
            try
            {
                // The terms a week is made under are the venue's as they stand now, and they
                // stand still for the length of a sweep — so they are read once for the venue
                // rather than once for every week it is about to make (PRD BR-05).
                var policyId = await BookingEndpoints.InForcePolicyIdAsync(
                    database, venue.Key, cancellationToken);

                var answered = await AnsweredAsync(venue, today, cancellationToken);

                foreach (var series in venue)
                {
                    made += await FillAsync(
                        series,
                        answered.GetValueOrDefault(series.Id) ?? [],
                        today,
                        until,
                        policyId,
                        now,
                        cancellationToken);
                }
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                loggers.CreateLogger<SeriesBookings>().LogError(
                    failure,
                    "Could not fill in the weeks standing at venue {VenueId} this time.",
                    venue.Key);
            }
        }

        return made;
    }

    /// <summary>
    /// Which weeks each of a venue's arrangements has already been answered on — booked, or
    /// written down as missed. Two queries for the venue rather than two for every arrangement:
    /// on most sweeps every week is already answered and this is the whole of the work.
    /// </summary>
    private async Task<Dictionary<Guid, HashSet<DateOnly>>> AnsweredAsync(
        IEnumerable<BookingSeries> forVenue,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var ids = forVenue.Select(one => one.Id).ToList();
        var from = PlatformRequirements.BangkokHour(today, 0);

        // A booking's hours say which week it is: an arrangement makes one booking a week, and
        // its hours are that week's. The booking itself is the record that the week was made —
        // there is no separate row saying so, which is what makes a sweep that stopped halfway
        // safe to run again.
        var booked = await database.BookingSlots
            .AsNoTracking()
            .Where(slot =>
                slot.Booking!.SeriesId != null
                && ids.Contains(slot.Booking.SeriesId.Value)
                && slot.StartsAt >= from)
            .Select(slot => new { SeriesId = slot.Booking!.SeriesId!.Value, slot.StartsAt })
            .ToListAsync(cancellationToken);

        var missed = await database.SeriesMisses
            .AsNoTracking()
            .Where(miss => ids.Contains(miss.SeriesId) && miss.Date >= today)
            .Select(miss => new { miss.SeriesId, miss.Date })
            .ToListAsync(cancellationToken);

        var answered = new Dictionary<Guid, HashSet<DateOnly>>();

        foreach (var week in booked)
        {
            Weeks(answered, week.SeriesId)
                .Add(PlatformRequirements.BangkokDateAndHour(week.StartsAt).Date);
        }

        foreach (var week in missed)
        {
            Weeks(answered, week.SeriesId).Add(week.Date);
        }

        return answered;
    }

    private static HashSet<DateOnly> Weeks(
        Dictionary<Guid, HashSet<DateOnly>> answered,
        Guid seriesId)
    {
        if (!answered.TryGetValue(seriesId, out var weeks))
        {
            weeks = [];
            answered[seriesId] = weeks;
        }

        return weeks;
    }

    /// <summary>
    /// The weeks of one arrangement that are inside the window and have not been answered yet.
    /// Both kinds of answer are readable afterwards, which is what makes a sweep that stopped
    /// halfway safe to run again.
    /// </summary>
    private async Task<int> FillAsync(
        BookingSeries series,
        HashSet<DateOnly> answered,
        DateOnly today,
        DateOnly until,
        Guid policyId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var made = 0;

        foreach (var date in Series.Dates(series, today, until)
                     .Where(date => !answered.Contains(date))
                     // An arrangement agreed on a Tuesday evening for Tuesdays does not book the
                     // Tuesday that is already over. It is not written down as a week that was
                     // missed either: nobody could have had it, so nothing was lost.
                     .Where(date =>
                         PlatformRequirements.BangkokHour(date, series.FromHour) > now))
        {
            if (await MakeAsync(series, date, policyId, now, cancellationToken))
            {
                made++;
            }
        }

        return made;
    }

    /// <summary>
    /// One week. Answers whether it was booked; where it was not, the reason is written down for
    /// the venue and not tried again.
    /// </summary>
    private async Task<bool> MakeAsync(
        BookingSeries series,
        DateOnly date,
        Guid policyId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var hours = Enumerable
            .Range(series.FromHour, series.Hours)
            .Select(hour => new BookingSlotRequest(series.CourtId, date, hour))
            .ToArray();

        var priced = await BookingEndpoints.PriceSlotsAsync(
            database, series.VenueId, hours, now, loggers, cancellationToken);

        var refusal = priced.Error;

        if (refusal is null)
        {
            var booking = Booking.ForSeries(series, policyId, priced.Slots, now);

            refusal = await BookingEndpoints.WriteNewAsync(
                database, booking, timeProvider, cancellationToken);

            if (refusal is null)
            {
                AppEvents.For(loggers).LogInformation(
                    BookingTransitions.EventName(BookingStatus.Confirmed)
                    + " {BookingId} {VenueId} {Channel} {Slots} {TotalBaht}",
                    booking.Id,
                    series.VenueId,
                    booking.Channel,
                    booking.Slots.Count,
                    booking.TotalBaht);

                AppEvents.For(loggers).LogInformation(
                    "series_week_booked {SeriesId} {BookingId} {Date}",
                    series.Id,
                    booking.Id,
                    date);

                return true;
            }
        }

        // Somebody took the hours — but it may have been this arrangement, from another
        // instance sweeping at the same moment. A week that is made is not a week to write off,
        // and the row saying so could never be taken back (it is append-only).
        if (refusal == BookingErrorCodes.SlotJustTaken
            && await HasWeekAsync(series.Id, date, cancellationToken))
        {
            return false;
        }

        await NoticeAsync(series.Id, date, refusal, now, cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "series_week_missed {SeriesId} {Date} {Refusal}", series.Id, date, refusal);

        return false;
    }

    /// <summary>Whether this arrangement already holds hours on that day.</summary>
    private async Task<bool> HasWeekAsync(
        Guid seriesId,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var from = PlatformRequirements.BangkokHour(date, 0);

        return await database.BookingSlots
            .AsNoTracking()
            .AnyAsync(
                slot => slot.Booking!.SeriesId == seriesId
                    && slot.StartsAt >= from
                    && slot.StartsAt < from.AddDays(1),
                cancellationToken);
    }

    /// <summary>
    /// Writes down a week that could not be had. Insert-or-nothing, because two instances sweeping
    /// at the same moment would otherwise both try to say it, and the row is append-only so the
    /// second could not be a correction of the first anyway.
    /// </summary>
    private async Task NoticeAsync(
        Guid seriesId,
        DateOnly date,
        string refusal,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO "SeriesMisses" ("Id", "SeriesId", "Date", "Refusal", "NoticedAt")
            VALUES ({Guid.CreateVersion7()}, {seriesId}, {date}, {refusal}, {now})
            ON CONFLICT ("SeriesId", "Date") DO NOTHING
            """,
            cancellationToken);
}
