using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Localization;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>
/// One day at one venue, as the settings in force on that date describe it: which courts were in
/// use, which hours the venue was open, what each hour costs, and which of them are already taken.
///
/// It is the one answer to "what does this hour cost and can I have it". The grid a booker reads
/// and the write that takes an hour both come through here, so they cannot disagree — and neither
/// has to know which tables that took.
/// </summary>
public sealed class VenueDay
{
    private readonly IReadOnlyDictionary<(Guid CourtId, int Hour), decimal?> prices;
    private readonly IReadOnlySet<(Guid CourtId, int Hour)> taken;
    private readonly IReadOnlySet<Guid> inUse;
    private readonly IReadOnlySet<(Guid CourtId, int Hour)> shut;

    private VenueDay(
        DateOnly date,
        IReadOnlyList<Court> courts,
        int? opensHour,
        int? closesHour,
        IReadOnlySet<Guid> inUse,
        IReadOnlySet<(Guid CourtId, int Hour)> shut,
        IReadOnlyDictionary<(Guid CourtId, int Hour), decimal?> prices,
        IReadOnlySet<(Guid CourtId, int Hour)> taken)
    {
        Date = date;
        Courts = courts;
        OpensHour = opensHour;
        ClosesHour = closesHour;
        this.inUse = inUse;
        this.shut = shut;
        this.prices = prices;
        this.taken = taken;
    }

    public DateOnly Date { get; }

    /// <summary>The venue's courts, in the order it lists them.</summary>
    public IReadOnlyList<Court> Courts { get; }

    public int? OpensHour { get; }

    public int? ClosesHour { get; }

    /// <summary>The hours the venue is open that day, which are the columns of the grid.</summary>
    public IEnumerable<int> Hours =>
        OpensHour is int from && ClosesHour is int until
            ? Enumerable.Range(from, until - from)
            : [];

    /// <summary>
    /// What the hour costs, or null when it is not on sale — because a court out of use sells
    /// nothing, because the court is shut for that hour (PRD US-11), or because nothing prices
    /// it, which is every hour of a venue that published its opening hours before its prices.
    /// </summary>
    public decimal? Price(Guid courtId, int hour) =>
        inUse.Contains(courtId) && !shut.Contains((courtId, hour))
            ? prices.GetValueOrDefault((courtId, hour))
            : null;

    /// <summary>
    /// Whether the venue had this court-hour to sell at all: the court in use, the venue open,
    /// and the court not shut (PRD US-15). Unlike <see cref="Price"/> it does not ask what the
    /// hour costs — the prices on hand are today's, and a report about last month must not
    /// depend on them.
    /// </summary>
    public bool IsSellable(Guid courtId, int hour) =>
        inUse.Contains(courtId)
        && OpensHour <= hour && hour < ClosesHour
        && !shut.Contains((courtId, hour));

    /// <summary>
    /// Whether the hour can be taken, and if not, why. An hour with nothing to charge for it is not
    /// for sale, whatever the court is doing.
    /// </summary>
    public HourStatus Status(Guid courtId, int hour) =>
        Price(courtId, hour) is null
            ? HourStatus.Closed
            : taken.Contains((courtId, hour))
                ? HourStatus.Booked
                : HourStatus.Free;

    /// <summary>
    /// Reads the day. Five queries, whatever the day holds: the courts, the two timelines that
    /// describe them, the prices in force, and the hours already taken.
    /// </summary>
    public static async Task<VenueDay> LoadAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly date,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var courts = await database.Courts
            .AsNoTracking()
            .Where(court => court.VenueId == venueId)
            .ToListAsync(cancellationToken);

        return await LoadAsync(database, venueId, courts, date, now, cancellationToken);
    }

    /// <summary>For a caller that already holds the venue's courts and wants several days of them.</summary>
    public static async Task<VenueDay> LoadAsync(
        AppDbContext database,
        Guid venueId,
        IReadOnlyList<Court> courts,
        DateOnly date,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var statusChanges = await CourtEndpoints.StatusChangesAsync(
            database, venueId, date, cancellationToken);
        var week = await CourtEndpoints.ScheduleOnAsync(database, venueId, date, cancellationToken);
        var closures = await ClosuresOnAsync(database, venueId, date, cancellationToken);
        var prices = await PricingEndpoints.InForcePricesAsync(database, venueId, cancellationToken);
        var courtIds = courts.Select(court => court.Id).ToArray();
        var taken = await BookedSlots.OnAsync(database, courtIds, date, now, cancellationToken);

        return Build(date, courts, statusChanges, closures, week, prices?.Bands ?? [], taken);
    }

    /// <summary>The rule itself, with everything it needs already read. Pure, so it is testable.</summary>
    public static VenueDay Build(
        DateOnly date,
        IReadOnlyList<Court> courts,
        IReadOnlyCollection<CourtStatusChange> statusChanges,
        IReadOnlyCollection<CourtClosure> closures,
        OpeningHoursSchedule? week,
        IReadOnlyCollection<PriceBand> bands,
        IReadOnlySet<(Guid CourtId, int Hour)> taken)
    {
        var day = week?.Days.SingleOrDefault(entry => entry.Day == date.DayOfWeek);
        var ordered = courts
            .OrderBy(court => court.Position)
            .ThenBy(court => court.Name)
            .ToList();

        var statusByCourt = statusChanges.ToLookup(change => change.CourtId);
        var inUse = ordered
            .Where(court => VenueTimeline.CourtIsActiveOn(statusByCourt[court.Id], court.Id, date))
            .Select(court => court.Id)
            .ToHashSet();

        // What an hour costs is the venue's, not the court's, so each hour is priced once.
        var hours = day?.OpensHour is int from && day.ClosesHour is int until
            ? Enumerable.Range(from, until - from)
            : [];

        // A court shut for part of the day sells nothing for those hours and everything else
        // as usual, so this is per court-hour rather than per court (PRD US-11).
        var shut = new HashSet<(Guid CourtId, int Hour)>();
        foreach (var closure in closures)
        {
            foreach (var hour in hours)
            {
                if (closure.Covers(date, hour))
                {
                    shut.Add((closure.CourtId, hour));
                }
            }
        }

        var prices = new Dictionary<(Guid CourtId, int Hour), decimal?>();
        foreach (var hour in hours)
        {
            var baht = PriceFor(bands, date.DayOfWeek, hour);
            foreach (var court in ordered)
            {
                prices[(court.Id, hour)] = baht;
            }
        }

        return new VenueDay(
            date, ordered, day?.OpensHour, day?.ClosesHour, inUse, shut, prices, taken);
    }

    /// <summary>
    /// The closures that could touch a day at this venue: anything still standing that overlaps
    /// it at all. Read here rather than at each call site so the grid and the write that takes an
    /// hour cannot disagree about which hours are shut.
    /// </summary>
    public static async Task<List<CourtClosure>> ClosuresOnAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var from = PlatformRequirements.BangkokHour(date, 0);
        var until = PlatformRequirements.BangkokHour(date.AddDays(1), 0);

        return await database.CourtClosures
            .AsNoTracking()
            .Where(closure =>
                closure.Court!.VenueId == venueId
                && closure.LiftedAt == null
                && closure.StartsAt < until
                && closure.EndsAt > from)
            .ToListAsync(cancellationToken);
    }

    private static decimal? PriceFor(IReadOnlyCollection<PriceBand> bands, DayOfWeek day, int hour) =>
        bands
            .Where(band => band.Day == day && band.FromHour <= hour && hour < band.ToHour)
            .Select(band => (decimal?)band.BahtPerHour)
            .FirstOrDefault();
}
