using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Jobs;

/// <summary>
/// Hours that ran out (PRD US-31, ⚠️ S-28).
///
/// A package is good until a day, and on the day after it the hours left on it stop being hours
/// the venue owes. That is a thing the clock does and nobody asks for, so it is the caretaker's
/// work — and it is written down as a movement like every other, because a balance that fell to
/// nothing with no row saying why is a balance somebody will argue about.
///
/// The row is claimed before it is written, the same way the slip reminder claims its booking: a
/// second instance, or the next tick, finds nothing left to do rather than writing it off twice.
/// </summary>
public sealed class PackageExpiry(
    AppDbContext database,
    TimeProvider timeProvider,
    ILoggerFactory loggers)
{
    /// <summary>Writes off what is left on every package whose day has passed. Answers how many.</summary>
    public async Task<int> WorkAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var today = PlatformRequirements.BangkokToday(timeProvider);

        var over = await database.HourPackages
            .AsNoTracking()
            .Where(one => one.ExpiredAt == null && one.ExpiresOn < today)
            .Select(one => new { one.Id, one.VenueId })
            .ToListAsync(cancellationToken);

        var written = 0;

        foreach (var package in over)
        {
            // The claim and the row that explains it are one write. Two of them would leave a
            // package marked finished with hours still on it and nothing saying where they went —
            // which is the thing this file exists to prevent — and nothing would ever look at it
            // again, because the next sweep only reads the ones still unclaimed.
            await using var transaction =
                await database.Database.BeginTransactionAsync(cancellationToken);

            // Behind the package's own lock, so a sale spending the last of its hours at this
            // moment is either counted here or waits for this to finish (PRD US-26's rule).
            await Locks.OnAsync(database, package.Id, cancellationToken);

            // Claimed first. Whoever sets this is the one who writes the row; anybody else who
            // was about to finds it taken and leaves it alone.
            var claimed = await database.HourPackages
                .Where(one => one.Id == package.Id && one.ExpiredAt == null)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(one => one.ExpiredAt, now),
                    cancellationToken);

            if (claimed == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }

            var left = await PackageEndpoints.BalanceAsync(database, package.Id, cancellationToken);
            if (left > 0)
            {
                database.PackageEntries.Add(new PackageEntry
                {
                    PackageId = package.Id,
                    Hours = -left,
                    Move = PackageMove.Expired,

                    // Nobody: the day passed. That is what a null actor means everywhere else
                    // (PRD 6.1), and it is true here.
                    At = now,
                    ByUserId = null,
                });

                await database.SaveChangesAsync(cancellationToken);
                written++;
            }

            await transaction.CommitAsync(cancellationToken);

            if (left > 0)
            {
                AppEvents.For(loggers).LogInformation(
                    "package_expired {PackageId} {VenueId} {Hours}",
                    package.Id,
                    package.VenueId,
                    left);
            }
        }

        return written;
    }
}
