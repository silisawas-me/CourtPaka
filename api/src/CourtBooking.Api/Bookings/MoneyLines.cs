using CourtBooking.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// One movement of money on a venue's day, as the drawer page writes it (thai-fit T2 artboard
/// "ปิดยอด"): what it was for, whose it was, and who took it. Sent as data — kinds, names and
/// numbers — and worded by the screen (US-23).
/// </summary>
public sealed record MoneyLineResponse(
    DateTimeOffset At,
    /// <summary>True for money leaving the venue (a refund, a bill, a sale handed back).</summary>
    bool Out,
    /// <summary>In: <c>Court</c>, <c>Sale</c>, <c>Package</c>. Out: a <see cref="CashOutKind"/>.</summary>
    string Kind,
    string Method,
    decimal AmountBaht,
    /// <summary>Who it was for: the customer's name, as the counter calls them.</summary>
    string? Who,
    /// <summary>The courts of the booking it paid for, by their names.</summary>
    string? Courts,
    /// <summary>The booking's kind (<see cref="BookingKinds"/>), for a court payment or a refund.</summary>
    string? BookingKind,
    /// <summary><c>Deposit</c> or <c>Rest</c> where a booking was paid in more than one go.</summary>
    string? Part,
    /// <summary>What a sale was, each line as its name and how many.</summary>
    MoneyLineItem[]? Items,
    /// <summary>How many hours a package sold carried.</summary>
    int? PackageHours,
    /// <summary>What kind of bill a pay-out was (<see cref="SpendKind"/>).</summary>
    string? SpendKind,
    string? Note,
    /// <summary>Who took it or wrote it down; null where nobody at the venue did (a slip).</summary>
    string? By,
    /// <summary>The booking it was for or about, where there is one.</summary>
    Guid? BookingId = null);

public sealed record MoneyLineItem(string Name, int Quantity);

public static class MoneyLines
{
    /// <summary>
    /// Every receipt the day counts, and every cash that left the drawer in its window, in the
    /// order they happened. The receipts are the same rows the day's totals add up; the cash out
    /// is the same list the expected cash subtracts.
    /// </summary>
    public static async Task<MoneyLineResponse[]> ForDayAsync(
        AppDbContext database,
        Guid venueId,
        DateOnly day,
        IReadOnlyCollection<CashOut> cashOut,
        CancellationToken cancellationToken)
    {
        var receipts = await database.PaymentReceipts
            .AsNoTracking()
            .Where(receipt => receipt.VenueId == venueId && receipt.CountsOn == day)
            .Select(receipt => new
            {
                receipt.BookingId,
                receipt.PackageId,
                receipt.SaleId,
                receipt.AmountBaht,
                receipt.Method,
                receipt.ReceivedAt,
                receipt.Note,
                receipt.ReceivedByUserId,
            })
            .ToListAsync(cancellationToken);

        var bookingIds = receipts
            .Select(receipt => receipt.BookingId)
            .Concat(cashOut.Select(one => one.BookingId))
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        var saleIds = receipts.Select(receipt => receipt.SaleId).OfType<Guid>().Distinct().ToArray();
        var packageIds = receipts.Select(receipt => receipt.PackageId).OfType<Guid>().Distinct().ToArray();

        var sales = await database.ShopSales
            .AsNoTracking()
            .Where(sale => saleIds.Contains(sale.Id))
            .Select(sale => new
            {
                sale.Id,
                sale.BookingId,
                Items = sale.Lines.Select(line => new MoneyLineItem(line.Name, line.Quantity)).ToArray(),
            })
            .ToDictionaryAsync(sale => sale.Id, cancellationToken);

        // A sale written onto a booking is that booking's party; its name comes from there.
        var allBookings = bookingIds
            .Concat(sales.Values.Select(sale => sale.BookingId).OfType<Guid>())
            .Distinct()
            .ToArray();
        var bookings = await database.Bookings
            .AsNoTracking()
            .Include(booking => booking.Slots)
            .Where(booking => allBookings.Contains(booking.Id))
            .Select(booking => new
            {
                Booking = booking,
                Name = booking.CustomerName
                       ?? (booking.Booker!.DeletedAt == null ? booking.Booker.DisplayName : null),
            })
            .ToDictionaryAsync(row => row.Booking.Id, cancellationToken);

        var courtNames = await database.Courts
            .Where(court => court.VenueId == venueId)
            .ToDictionaryAsync(court => court.Id, court => court.Name, cancellationToken);

        // Which payment of its booking each receipt was: the first of several is the deposit.
        var paidInParts = await database.PaymentReceipts
            .AsNoTracking()
            .Where(receipt => receipt.BookingId != null && bookingIds.Contains(receipt.BookingId.Value))
            .GroupBy(receipt => receipt.BookingId!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => new { BookingId = group.Key, First = group.Min(receipt => receipt.ReceivedAt) })
            .ToDictionaryAsync(group => group.BookingId, group => group.First, cancellationToken);

        var packages = await database.HourPackages
            .AsNoTracking()
            .Where(package => packageIds.Contains(package.Id))
            .ToDictionaryAsync(package => package.Id, cancellationToken);

        var people = receipts
            .Select(receipt => receipt.ReceivedByUserId)
            .Concat(cashOut.Select(one => one.ByUserId))
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        var names = await database.Users
            .Where(user => people.Contains(user.Id))
            .Select(user => new { user.Id, Name = user.DeletedAt == null ? user.DisplayName ?? user.Email : null })
            .ToDictionaryAsync(user => user.Id, user => user.Name, cancellationToken);

        string? NameOf(Guid? userId) => userId is { } id ? names.GetValueOrDefault(id) : null;

        string? CourtsOf(Guid? bookingId) =>
            bookingId is { } id && bookings.TryGetValue(id, out var row)
                ? string.Join(", ", row.Booking.Slots
                    .Select(slot => slot.CourtId)
                    .Distinct()
                    .Select(court => courtNames.GetValueOrDefault(court, string.Empty)))
                : null;

        var lines = new List<MoneyLineResponse>();
        foreach (var receipt in receipts)
        {
            if (receipt.SaleId is { } saleId && sales.TryGetValue(saleId, out var sale))
            {
                var party = sale.BookingId is { } on && bookings.TryGetValue(on, out var row) ? row.Name : null;
                lines.Add(new MoneyLineResponse(
                    receipt.ReceivedAt, false, "Sale", receipt.Method.ToString(), receipt.AmountBaht,
                    party, null, null, null, sale.Items, null, null, receipt.Note,
                    NameOf(receipt.ReceivedByUserId), sale.BookingId));
            }
            else if (receipt.PackageId is { } packageId && packages.TryGetValue(packageId, out var package))
            {
                lines.Add(new MoneyLineResponse(
                    receipt.ReceivedAt, false, "Package", receipt.Method.ToString(), receipt.AmountBaht,
                    package.CustomerName, null, null, null, null, package.HoursSold, null, receipt.Note,
                    NameOf(receipt.ReceivedByUserId)));
            }
            else if (receipt.BookingId is { } bookingId && bookings.TryGetValue(bookingId, out var row))
            {
                var part = paidInParts.TryGetValue(bookingId, out var first)
                    ? (receipt.ReceivedAt == first ? "Deposit" : "Rest")
                    : null;
                lines.Add(new MoneyLineResponse(
                    receipt.ReceivedAt, false, "Court", receipt.Method.ToString(), receipt.AmountBaht,
                    row.Name, CourtsOf(bookingId), BookingKinds.Of(row.Booking), part, null, null, null,
                    receipt.Note, NameOf(receipt.ReceivedByUserId), bookingId));
            }
        }

        foreach (var one in cashOut)
        {
            var row = one.BookingId is { } id ? bookings.GetValueOrDefault(id) : null;
            lines.Add(new MoneyLineResponse(
                one.At, true, one.Kind.ToString(), nameof(PaymentMethod.Cash), one.AmountBaht,
                row?.Name, CourtsOf(one.BookingId), row is null ? null : BookingKinds.Of(row.Booking),
                null, null, null, one.SpendKind?.ToString(), one.Note, NameOf(one.ByUserId), one.BookingId));
        }

        return [.. lines.OrderBy(line => line.At)];
    }
}
