using CourtBooking.Api.Bookings;
using CourtBooking.Api.Data;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Documents;

/// <summary>
/// What a booking's receipt would say (thai-fit T6) — a preview, not a document. Which kind of
/// paper it is follows the venue's VAT registration, never a choice at the desk: a venue that is
/// not registered gives ใบเสร็จรับเงิน and must not print "ใบกำกับภาษี" or a VAT line (ม.86/13);
/// one that is gives ใบกำกับภาษีอย่างย่อ, and the full invoice when the customer asks.
///
/// Nothing is issued: no number is taken from <see cref="DocumentNumbers"/> — that sequence may
/// not skip (PRD 7.4) and a preview printed and thrown away would leave a hole — and nothing is
/// stored. The page says so on every copy until the accountant answers Q1.
/// </summary>
public static class ReceiptPreview
{
    /// <summary>Thailand's VAT, as the share a VAT-inclusive price holds: 7 of every 107.</summary>
    public const decimal VatRate = 0.07m;

    public static void MapReceiptPreviewEndpoints(this RouteGroupBuilder bookings) =>
        // Inside the bookings group: ManageBookings, open at a suspended venue (what was sold
        // still gets its paper), and the customer's name is on it (PDPA, US-13).
        bookings.MapGet("/{bookingId:guid}/receipt", PreviewAsync);

    /// <summary>The kind of paper a venue gives, from its VAT registration alone.</summary>
    public static DocumentKind KindFor(bool vatRegistered) =>
        vatRegistered ? DocumentKind.Abb : DocumentKind.Rec;

    /// <summary>
    /// A VAT-inclusive total split into its value and its tax, to the satang. The tax is what is
    /// rounded; the value is the rest, so the two always add back to what was paid.
    /// </summary>
    public static (decimal BeforeVat, decimal Vat) SplitVat(decimal total)
    {
        var vat = Math.Round(total * VatRate / (1 + VatRate), 2, MidpointRounding.AwayFromZero);
        return (total - vat, vat);
    }

    private static async Task<Results<Ok<ReceiptPreviewResponse>, NotFound>> PreviewAsync(
        Guid venueId,
        Guid bookingId,
        CurrentVenue current,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var booking = await database.Bookings
            .AsNoTracking()
            .Where(one => one.Id == bookingId && one.VenueId == venueId)
            .Select(one => new
            {
                one.TotalBaht,
                one.CustomerName,
                BookerName = one.Booker != null && one.Booker.DeletedAt == null ? one.Booker.DisplayName : null,
                Slots = one.Slots
                    .OrderBy(slot => slot.StartsAt)
                    .Select(slot => new { CourtName = slot.Court!.Name, slot.StartsAt, slot.EndsAt, slot.BahtPerHour })
                    .ToList(),
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
        {
            return TypedResults.NotFound();
        }

        var venue = await database.Venues
            .AsNoTracking()
            .Where(one => one.Id == venueId)
            .Select(one => new { one.Code, one.Name, one.Business })
            .FirstAsync(cancellationToken);

        var dayStarts = current.DayStartsHour;

        // The court hours, one line per court in a row: "คอร์ต 1 · 19:00–21:00", hours, money.
        var lines = new List<ReceiptLineResponse>();
        foreach (var run in Runs(booking.Slots.Select(slot => (slot.CourtName, slot.StartsAt, slot.EndsAt, slot.BahtPerHour))))
        {
            lines.Add(new ReceiptLineResponse(
                nameof(ReceiptLineKind.Court), run.CourtName, run.StartsAt, run.EndsAt, run.Hours, run.Baht));
        }

        // What the desk sold onto the booking and has not taken back.
        var sold = await database.ShopSales
            .AsNoTracking()
            .Where(sale => sale.BookingId == bookingId && sale.VenueId == venueId && sale.CancelledAt == null)
            .SelectMany(sale => sale.Lines)
            .GroupBy(line => new { line.Name, line.EachBaht })
            .Select(group => new { group.Key.Name, Quantity = group.Sum(line => line.Quantity), group.Key.EachBaht })
            .ToListAsync(cancellationToken);
        lines.AddRange(sold
            .OrderBy(line => line.Name)
            .Select(line => new ReceiptLineResponse(
                nameof(ReceiptLineKind.Item), line.Name, null, null, line.Quantity, line.EachBaht * line.Quantity)));

        var methods = await database.PaymentReceipts
            .AsNoTracking()
            .Where(receipt =>
                receipt.VenueId == venueId
                && (receipt.BookingId == bookingId
                    || (receipt.SaleId != null && database.ShopSales.Any(sale =>
                        sale.Id == receipt.SaleId && sale.BookingId == bookingId && sale.CancelledAt == null))))
            .Select(receipt => receipt.Method)
            .Distinct()
            .ToListAsync(cancellationToken);

        var total = lines.Sum(line => line.AmountBaht);
        var business = venue.Business;
        var kind = KindFor(business.IsVatRegistered);
        var (beforeVat, vat) = business.IsVatRegistered ? SplitVat(total) : (total, 0m);
        var today = VenueClock.Today(timeProvider, dayStarts);

        return TypedResults.Ok(new ReceiptPreviewResponse(
            kind.ToString(),
            // The shape of the number this paper would carry, without the sequence it may not
            // take: "ARI01-ABB-2026".
            $"{venue.Code}-{kind.ToString().ToUpperInvariant()}-{today.Year:0000}",
            today,
            new ReceiptSellerResponse(
                venue.Name,
                business.LegalName,
                business.TaxId,
                business.TaxBranch,
                business.BillingAddress,
                business.IsVatRegistered),
            booking.CustomerName ?? booking.BookerName,
            [.. lines],
            total,
            beforeVat,
            vat,
            [.. methods.Select(method => method.ToString()).Order()]));
    }

    private sealed record Run(string CourtName, DateTimeOffset StartsAt, DateTimeOffset EndsAt, int Hours, decimal Baht);

    /// <summary>Hours that follow on, on the same court, as one line.</summary>
    private static IEnumerable<Run> Runs(
        IEnumerable<(string CourtName, DateTimeOffset StartsAt, DateTimeOffset EndsAt, decimal BahtPerHour)> slots)
    {
        Run? open = null;
        foreach (var slot in slots.OrderBy(slot => slot.CourtName).ThenBy(slot => slot.StartsAt))
        {
            if (open is not null && open.CourtName == slot.CourtName && open.EndsAt == slot.StartsAt)
            {
                open = open with { EndsAt = slot.EndsAt, Hours = open.Hours + 1, Baht = open.Baht + slot.BahtPerHour };
                continue;
            }

            if (open is not null)
            {
                yield return open;
            }

            open = new Run(slot.CourtName, slot.StartsAt, slot.EndsAt, 1, slot.BahtPerHour);
        }

        if (open is not null)
        {
            yield return open;
        }
    }
}

/// <summary>A court line or a line off the counter's shelf.</summary>
public enum ReceiptLineKind
{
    Court,
    Item,
}

/// <summary>One line of the paper: what, how many, how much. Times only for a court.</summary>
public sealed record ReceiptLineResponse(
    /// <summary>A <see cref="ReceiptLineKind"/> by name.</summary>
    string Kind,
    string Name,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    int Quantity,
    decimal AmountBaht);

/// <summary>Who sells: the venue in its own name. The platform is never on the paper.</summary>
public sealed record ReceiptSellerResponse(
    string Name,
    string LegalName,
    string TaxId,
    string TaxBranch,
    string Address,
    bool VatRegistered);

/// <summary>
/// A booking's receipt as it would be printed (thai-fit T6). <c>Kind</c> is the name of a
/// <see cref="DocumentKind"/> (<c>Rec</c> or <c>Abb</c>; the full invoice is the same numbers with
/// the buyer added at the desk). The money is the server's: lines, total, and the VAT inside it.
/// </summary>
public sealed record ReceiptPreviewResponse(
    string Kind,
    string NumberPrefix,
    DateOnly Date,
    ReceiptSellerResponse Seller,
    string? CustomerName,
    ReceiptLineResponse[] Lines,
    decimal TotalBaht,
    decimal BeforeVatBaht,
    decimal VatBaht,
    string[] PaidBy);
