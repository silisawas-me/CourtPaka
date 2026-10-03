using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Documents;
using CourtBooking.Api.Localization;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Venues;

/// <summary>One booking as a month's billing sees it, before it becomes a line.</summary>
public readonly record struct Billable(
    Guid BookingId,
    DateOnly ServedOn,
    decimal KeptBaht);

/// <summary>
/// The month's billing (PRD US-21, BR-08): what each venue owes the platform for the month just
/// gone, and the invoice that says so.
///
/// The rule it follows has three conditions, and the third is the one that makes the run safe to
/// repeat: a booking already on an invoice is never billed again. So a run that half-finished, or
/// a run started twice, adds nothing the first one already covered — which matters more than
/// usual here, because this is money the platform asks a venue for.
///
/// The cut-off is a fixed instant, not the moment the job happens to wake up. A job that ran
/// late would otherwise bill a booking that settled in between, and the same month run twice
/// would come to two different numbers.
/// </summary>
public static class CommissionRun
{
    /// <summary>
    /// The instant a month's billing looks at the world: the 2nd of the following month at
    /// 02:00, the venue's time (PRD BR-08, BR-10). Fixed, so the answer does not depend on when
    /// the job woke up.
    /// </summary>
    public static DateTimeOffset CutOff(DateOnly month) =>
        PlatformRequirements.BangkokHour(FirstOfNext(month).AddDays(1), 2);

    /// <summary>When it has to be paid: the 16th of the month it was issued in (PRD BR-09).</summary>
    public static DateOnly DueOn(DateOnly month) =>
        FirstOfNext(month).AddDays(15);

    /// <summary>The first day after the month being billed, which BR-08 measures against.</summary>
    public static DateOnly FirstOfNext(DateOnly month) =>
        new DateOnly(month.Year, month.Month, 1).AddMonths(1);

    /// <summary>
    /// Everything this venue may be billed for in this month's run: bookings played before the
    /// month was out, settled by the cut-off, and not already on an invoice (PRD BR-08).
    ///
    /// Bookings served before the month being billed are here too, and that is deliberate —
    /// something that settled late is carried forward rather than lost. They are charged at the
    /// rate in force on the day they were played, not on the day the invoice is made.
    /// </summary>
    public static async Task<List<Billable>> BillableAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly month,
        CancellationToken cancellationToken)
    {
        var cutOff = CutOff(month);
        // The month ends where the venue's day does: Friday 31st's 01:00 is still the 31st at a
        // venue open until 02:00 (thai-fit T4).
        var dayStartsHour = await VenueDay.DayStartsHourAsync(database, venueId, cancellationToken);
        var until = VenueClock.Window(FirstOfNext(month), dayStartsHour).From;

        var billed = database.CommissionInvoiceLines.Select(line => line.BookingId);

        // Only the channel the platform charges for (PRD BR-08, S-13), and only the endings that
        // can carry money: a booking still being played owes nothing yet.
        var candidates = await database.Bookings
            .AsNoTracking()
            .Where(booking =>
                booking.VenueId == venueId
                && booking.Channel == BookingChannel.Online
                && !billed.Contains(booking.Id)
                && (booking.Status == BookingStatus.Completed
                    || booking.Status == BookingStatus.NoShow
                    || booking.Status == BookingStatus.Cancelled
                    || booking.Status == BookingStatus.Confirmed))
            .Select(booking => new
            {
                booking.Id,
                booking.Status,
                booking.PaymentState,
                booking.TotalBaht,
                booking.RefundDueBaht,
                booking.DepositBaht,

                // What the hours cost, for a booking paid with them rather than with money — the
                // board's price that day is the one the customer chose not to pay (PRD US-31).
                // Only counter sales can be paid that way and only Online is billed, so this is
                // null today; it is asked for anyway, because the rule should not rest on that.
                PaidWithHours = booking.PackageId == null ? (decimal?)null : booking.PackageBaht,
                Taken = database.PaymentReceipts
                    .Where(receipt => receipt.BookingId == booking.Id)
                    .Sum(receipt => (decimal?)receipt.AmountBaht) ?? 0m,
                FirstStart = booking.Slots.Min(slot => slot.StartsAt),
                LastEnd = booking.Slots.Max(slot => slot.EndsAt),
            })
            .Where(booking => booking.FirstStart < until)
            .ToListAsync(cancellationToken);

        var billable = new List<Billable>();

        foreach (var booking in candidates)
        {
            // What a booking reads as, not what is stored: a confirmed one whose hours are
            // behind it is completed, and that is what settles it (PRD 9.2).
            var status = booking.Status == BookingStatus.Confirmed && booking.LastEnd <= cutOff
                ? BookingStatus.Completed
                : booking.Status;

            if (!Settled.By(status, booking.PaymentState, booking.LastEnd, cutOff))
            {
                continue;
            }

            var kept = Takings.KeptBy(
                booking.PaymentState,
                booking.TotalBaht,
                booking.RefundDueBaht,
                booking.Taken,
                booking.DepositBaht,
                booking.PaidWithHours);

            // A booking the venue kept nothing out of is nothing to charge a share of. It is
            // left off rather than written as a nought line, so an invoice's lines are the
            // bookings it is actually asking for money about.
            if (kept <= 0m)
            {
                continue;
            }

            billable.Add(new Billable(
                booking.Id,
                VenueClock.DayAndHour(booking.FirstStart, dayStartsHour).Date,
                kept));
        }

        return [.. billable.OrderBy(one => one.ServedOn).ThenBy(one => one.BookingId)];
    }

    /// <summary>
    /// Bills one venue for one month, or answers null where there is nothing to bill: PRD US-21
    /// says an invoice for nought is not issued, and a venue that owes nothing should not be
    /// sent a demand for nothing.
    ///
    /// The whole of it is one transaction, because the number comes from a counter that must not
    /// skip (PRD 7.4) — a number taken and then not written is a gap that cannot be closed.
    /// </summary>
    public static async Task<CommissionInvoice?> BillAsync(
        AppDbContext database,
        Venue venue,
        DateOnly month,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var billable = await BillableAsync(database, venue.Id, month, cancellationToken);
        if (billable.Count == 0)
        {
            return null;
        }

        var rates = await database.CommissionRates
            .AsNoTracking()
            .Where(rate => rate.VenueId == venue.Id)
            .ToListAsync(cancellationToken);

        var lines = new List<CommissionInvoiceLine>();
        foreach (var one in billable)
        {
            // No rate agreed is not a rate of nought: it is a question nobody has answered, and
            // billing nothing would answer it quietly and wrongly. The booking waits for a rate
            // and is picked up by the next run once there is one.
            if (Commission.InForceOn(rates, one.ServedOn) is not { } rate)
            {
                continue;
            }

            lines.Add(new CommissionInvoiceLine
            {
                InvoiceId = Guid.Empty,
                BookingId = one.BookingId,
                ServedOn = one.ServedOn,
                KeptBaht = one.KeptBaht,
                Percent = rate.Percent,
                AmountBaht = Commission.On(one.KeptBaht, rate.Percent),
            });
        }

        var amount = lines.Sum(line => line.AmountBaht);
        if (lines.Count == 0 || amount <= 0m)
        {
            return null;
        }

        var number = await DocumentNumbers.NextAsync(
            database,
            DocumentNumbers.PlatformSeries,
            DocumentKind.Inv,
            PlatformRequirements.BangkokDateAndHour(now).Date.Year,
            cancellationToken);

        var invoice = new CommissionInvoice
        {
            VenueId = venue.Id,
            Number = number,
            Month = new DateOnly(month.Year, month.Month, 1),
            AmountBaht = amount,
            IssuedAt = now,
            DueOn = DueOn(month),
        };

        invoice.Lines.AddRange(lines.Select(line => new CommissionInvoiceLine
        {
            InvoiceId = invoice.Id,
            BookingId = line.BookingId,
            ServedOn = line.ServedOn,
            KeptBaht = line.KeptBaht,
            Percent = line.Percent,
            AmountBaht = line.AmountBaht,
        }));

        database.CommissionInvoices.Add(invoice);
        return invoice;
    }
}
