using CourtBooking.Api.Data;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Jobs;

/// <summary>
/// The month's commission invoices (PRD US-21, BR-08): issued on the 2nd at 02:00, one per venue
/// that owes anything, and none for a venue that owes nothing.
///
/// Nothing here depends on the job waking up on time. It asks which months are unbilled and bills
/// them, so a platform that was switched off over the turn of the month catches up on the next
/// tick rather than skipping a month — and a tick that finds nothing to do does nothing, which is
/// what every tick but one a month will find.
///
/// Each venue is billed in its own transaction, because each takes a document number that must
/// not skip (PRD 7.4). One venue's failure leaves the others billed and itself unbilled, which
/// the next tick picks up.
/// </summary>
public sealed class MonthlyBilling(AppDbContext database, ILoggerFactory loggers)
{
    /// <summary>
    /// How far back a run will reach for a month nobody billed. A platform that was down for a
    /// week catches up; one that has never run does not silently invoice a year at once.
    /// </summary>
    public const int MonthsToCatchUp = 3;

    /// <summary>
    /// Bills every month that is due and not yet billed, and answers how many invoices it wrote.
    /// </summary>
    public async Task<int> IssueDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var written = 0;

        foreach (var month in DueMonths(now))
        {
            written += await IssueMonthAsync(month, now, cancellationToken);
        }

        return written;
    }

    /// <summary>
    /// The months whose cut-off has passed, newest last so a booking carried forward lands on the
    /// earliest invoice that could take it.
    /// </summary>
    public static IEnumerable<DateOnly> DueMonths(DateTimeOffset now)
    {
        var thisMonth = new DateOnly(
            PlatformRequirements.BangkokDateAndHour(now).Date.Year,
            PlatformRequirements.BangkokDateAndHour(now).Date.Month,
            1);

        for (var back = MonthsToCatchUp; back >= 1; back--)
        {
            var month = thisMonth.AddMonths(-back);
            if (CommissionRun.CutOff(month) <= now)
            {
                yield return month;
            }
        }
    }

    /// <summary>Bills one month for every venue that has not been billed for it yet.</summary>
    private async Task<int> IssueMonthAsync(
        DateOnly month,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var first = new DateOnly(month.Year, month.Month, 1);

        // Only the venues without an invoice for it. Asked once for the month rather than once
        // per venue, because most months most venues are already done.
        var billed = await database.CommissionInvoices
            .Where(invoice => invoice.Month == first)
            .Select(invoice => invoice.VenueId)
            .ToListAsync(cancellationToken);

        var venues = await database.Venues
            .AsNoTracking()
            .Where(venue => !billed.Contains(venue.Id))
            .ToListAsync(cancellationToken);

        var written = 0;

        foreach (var venue in venues)
        {
            if (await IssueOneAsync(venue, first, now, cancellationToken))
            {
                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// Bills one venue for one month. Its own transaction, and its own try/catch: the number it
    /// takes must not be taken and then dropped (PRD 7.4), and one venue that cannot be billed
    /// must not stop the rest of the platform being billed.
    /// </summary>
    private async Task<bool> IssueOneAsync(
        Venue venue,
        DateOnly month,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            database.ChangeTracker.Clear();
            await using var transaction =
                await database.Database.BeginTransactionAsync(cancellationToken);

            var invoice = await CommissionRun.BillAsync(
                database, venue, month, now, cancellationToken);

            if (invoice is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            AppEvents.For(loggers).LogInformation(
                "commission_invoiced {VenueId} {Number} {Month} {AmountBaht} {Lines}",
                venue.Id, invoice.Number, month, invoice.AmountBaht, invoice.Lines.Count);

            return true;
        }
        catch (Exception failure)
        {
            // Said out loud and left for the next tick. A venue that was not billed is a venue
            // the platform is owed by and has not asked — which is recoverable, and which
            // somebody has to know about.
            loggers.CreateLogger<MonthlyBilling>().LogError(
                failure,
                "Could not bill {VenueId} for {Month}.",
                venue.Id,
                month);
            return false;
        }
    }
}
