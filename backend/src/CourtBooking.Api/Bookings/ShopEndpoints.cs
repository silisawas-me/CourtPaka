using CourtBooking.Api.Data;
using CourtBooking.Api.Http;
using CourtBooking.Api.Localization;
using CourtBooking.Api.Observability;
using CourtBooking.Api.Venues;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// What a counter sells besides court time (PRD US-32), and what the venue paid out (US-33).
///
/// They are one thing, not two: the shuttlecocks that get sold are the shuttlecocks that had to
/// be bought, and the stock ledger is the answer to both. It is append-only, like the hours
/// ledger — what a venue has is the sum of what happened to it, and "where did they go" is a list
/// rather than a number somebody keeps.
///
/// Money from a sale is money in on the day it happens and goes through the same till day a count
/// closes (US-26), so there is no second way for money to reach the drawer. Money paid out in
/// cash leaves that same drawer, which is why an expense is part of the count.
/// </summary>
public static class ShopEndpoints
{
    public static void MapShopEndpoints(this RouteGroupBuilder venue)
    {
        var shop = venue.MapGroup("/shop");

        // Reading what was sold and what was paid out stays open at a suspended venue: money that
        // has already changed hands still has to be accounted for (PRD US-20).
        shop.RequireAuthorization(
            VenuePolicies.NeedsEvenWhenSuspended(VenuePermissions.ManageBookings));

        shop.MapGet("/items", BoardAsync);
        shop.MapGet("/sales", SalesAsync);
        shop.MapGet("/spending", SpendingAsync);

        // Selling is selling, which is what a suspension stops.
        shop.MapPost("/sales", SellAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageBookings));
        shop.MapPost("/sales/{saleId:guid}/cancel", CancelSaleAsync);

        // Money out, and counting the shelf. Neither is selling, so both stay open.
        shop.MapPost("/spending", SpendAsync);
        shop.MapPost("/spending/{spendId:guid}/void", VoidSpendAsync);
        shop.MapPost("/items/{itemId:guid}/count", CountAsync);

        // The board is a setting, like a price list (PRD US-14).
        shop.MapPost("/items", AddItemAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageSettings));
        shop.MapPost("/items/{itemId:guid}/withdraw", WithdrawItemAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageSettings));
    }

    /// <summary>
    /// What this venue sells, with how many are left of the ones it counts. Withdrawn lines come
    /// too, marked: a sale already made points at one, and somebody will ask what it was.
    /// </summary>
    private static async Task<Ok<ShopItemResponse[]>> BoardAsync(
        Guid venueId,
        AppDbContext database,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await ItemsAsync(database, venueId, cancellationToken));

    private static async Task<ShopItemResponse[]> ItemsAsync(
        AppDbContext database,
        Guid venueId,
        CancellationToken cancellationToken)
    {
        var items = await database.ShopItems
            .AsNoTracking()
            .Where(one => one.VenueId == venueId)
            .OrderBy(one => one.WithdrawnAt != null)
            .ThenBy(one => one.Name)
            .Select(one => new
            {
                Item = one,
                Left = database.StockEntries
                    .Where(entry => entry.ItemId == one.Id)
                    .Sum(entry => (int?)entry.Quantity) ?? 0,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. items.Select(one => new ShopItemResponse(
                one.Item.Id,
                one.Item.Name,
                one.Item.PriceBaht,
                one.Item.Unit,
                one.Item.Counted,
                one.Item.TellMeAt,
                // Nothing for what is not counted: a racquet to hire has no number, and a zero
                // beside it would read as none left (PRD US-32).
                one.Item.Counted ? one.Left : null,
                Shop.RunningLow(one.Item.Counted, one.Left, one.Item.TellMeAt),
                one.Item.WithdrawnAt)),
        ];
    }

    private static async Task<Results<Created<ShopItemResponse>, ProblemHttpResult>> AddItemAsync(
        Guid venueId,
        ShopItemRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();

        if (venue.Status != VenueStatus.Approved)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, VenueErrorCodes.NotApproved);
        }

        var price = request.PriceBaht ?? 0m;
        if (Shop.Refusal(request.Name, request.Unit, price, request.TellMeAt) is { } wrong)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, wrong);
        }

        var counted = request.Counted ?? false;
        var item = new ShopItem
        {
            VenueId = venueId,
            Name = request.Name!.Trim(),
            PriceBaht = price,
            Unit = request.Unit!.Trim(),
            Counted = counted,

            // Only a thing with a number can run low, so the floor goes with the counting.
            TellMeAt = counted ? request.TellMeAt : null,
            CreatedAt = timeProvider.GetUtcNow(),
            CreatedByUserId = membership.UserId,
        };

        database.ShopItems.Add(item);
        await database.SaveChangesAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "shop_item_added {ItemId} {VenueId} {PriceBaht}", item.Id, venueId, item.PriceBaht);

        return TypedResults.Created(
            $"/api/venues/{venueId}/shop/items",
            (await ItemsAsync(database, venueId, cancellationToken))
                .Single(one => one.ItemId == item.Id));
    }

    private static async Task<Results<Ok<ShopItemResponse>, ProblemHttpResult>> WithdrawItemAsync(
        Guid venueId,
        Guid itemId,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();
        var now = timeProvider.GetUtcNow();

        var withdrawn = await database.ShopItems
            .Where(one => one.Id == itemId && one.VenueId == venueId && one.WithdrawnAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(one => one.WithdrawnAt, now)
                    .SetProperty(one => one.WithdrawnByUserId, membership.UserId),
                cancellationToken);

        var drawn = (await ItemsAsync(database, venueId, cancellationToken))
            .SingleOrDefault(one => one.ItemId == itemId);

        if (drawn is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, ShopErrorCodes.ItemUnknown);
        }

        return withdrawn == 0
            ? ApiProblem.Of(StatusCodes.Status409Conflict, ShopErrorCodes.ItemAlreadyWithdrawn)
            : TypedResults.Ok(drawn);
    }

    /// <summary>What this venue has sold across the counter, newest first (PRD US-32).</summary>
    private static async Task<Ok<ShopSaleResponse[]>> SalesAsync(
        Guid venueId,
        DateOnly? date,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var day = date ?? PlatformRequirements.BangkokToday(timeProvider);
        var from = PlatformRequirements.BangkokHour(day, 0);
        var until = PlatformRequirements.BangkokHour(day.AddDays(1), 0);

        var sales = await database.ShopSales
            .AsNoTracking()
            .Where(one => one.VenueId == venueId && one.SoldAt >= from && one.SoldAt < until)
            .OrderByDescending(one => one.SoldAt)
            .Select(one => new
            {
                Sale = one,
                Lines = one.Lines
                    .OrderBy(line => line.Name)
                    .Select(line => new ShopSaleLineResponse(
                        line.ItemId, line.Name, line.Quantity, line.EachBaht))
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return TypedResults.Ok<ShopSaleResponse[]>(
        [
            .. sales.Select(one => new ShopSaleResponse(
                one.Sale.Id,
                one.Sale.BookingId,
                one.Sale.TotalBaht,
                one.Sale.SoldAt,
                one.Sale.CancelledAt,
                one.Sale.CancelReason,
                [.. one.Lines])),
        ]);
    }

    /// <summary>
    /// Rings up a sale (PRD US-32). The stock comes off and the money goes in, in one transaction
    /// and behind the same locks: a sale that took the money without the stock, or the stock
    /// without the money, is a shelf nobody can count.
    /// </summary>
    private static async Task<Results<Created<ShopSaleResponse>, ProblemHttpResult>> SellAsync(
        Guid venueId,
        ShopSaleRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();

        if (venue.Status != VenueStatus.Approved)
        {
            return ApiProblem.Of(StatusCodes.Status403Forbidden, VenueErrorCodes.NotApproved);
        }

        var asked = request.Lines ?? [];
        if (asked.Length is 0 or > Shop.MostLines
            || asked.Any(line => !Shop.IsAQuantity(line.Quantity))
            || asked.Select(line => line.ItemId).Distinct().Count() != asked.Length)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, ShopErrorCodes.NotASale);
        }

        if (request.PaidBy is not { } paidBy
            || !Enum.GetNames<PaymentMethod>().Contains(paidBy, StringComparer.Ordinal))
        {
            return ApiProblem.Of(
                StatusCodes.Status400BadRequest, BookingErrorCodes.InvalidCounterPayment);
        }

        // A sale may hang off a booking — the group that bought a tube halfway through their
        // hour — and it has to be one of this venue's.
        if (request.BookingId is { } bookingId
            && !await database.Bookings.AnyAsync(
                one => one.Id == bookingId && one.VenueId == venueId, cancellationToken))
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, ShopErrorCodes.BookingUnknown);
        }

        var now = timeProvider.GetUtcNow();

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        var wanted = asked.Select(line => line.ItemId).ToList();

        // Queued on every line, in one fixed order, before any of them is read: two counters
        // selling the last tube at the same moment would otherwise both read one left.
        foreach (var itemId in wanted.OrderBy(one => one))
        {
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({itemId.ToString()}, 0))",
                cancellationToken);
        }

        var items = await database.ShopItems
            .AsNoTracking()
            .Where(one => one.VenueId == venueId && wanted.Contains(one.Id))
            .ToListAsync(cancellationToken);

        if (items.Count != asked.Length || items.Any(one => one.WithdrawnAt is not null))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status404NotFound, ShopErrorCodes.ItemUnknown);
        }

        var left = await LeftAsync(database, wanted, cancellationToken);

        var sale = new ShopSale
        {
            VenueId = venueId,
            BookingId = request.BookingId,
            TotalBaht = asked.Sum(line =>
                items.Single(one => one.Id == line.ItemId).PriceBaht * line.Quantity),
            SoldAt = now,
            SoldByUserId = membership.UserId,
        };

        foreach (var line in asked)
        {
            var item = items.Single(one => one.Id == line.ItemId);

            // Nothing is sold that is not there. What is not counted has no number to be short
            // of — a racquet to hire is the venue's own business (PRD US-32).
            if (item.Counted && left.GetValueOrDefault(item.Id) < line.Quantity)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ApiProblem.Of(
                    StatusCodes.Status409Conflict, ShopErrorCodes.NotEnoughStock);
            }

            sale.Lines.Add(new ShopSaleLine
            {
                SaleId = sale.Id,
                ItemId = item.Id,

                // Copied in: the board can change tomorrow, and the receipt cannot (BR-05).
                Name = item.Name,
                Quantity = line.Quantity,
                EachBaht = item.PriceBaht,
            });

            if (item.Counted)
            {
                database.StockEntries.Add(new StockEntry
                {
                    ItemId = item.Id,
                    Quantity = -line.Quantity,
                    Move = StockMove.Sold,
                    SaleId = sale.Id,
                    At = now,
                    ByUserId = membership.UserId,
                });
            }
        }

        database.ShopSales.Add(sale);

        // The money is in the venue's hands as the line is rung up, so the day's count is told
        // about it the same way every other kind of money is (PRD US-26).
        database.PaymentReceipts.Add(new PaymentReceipt
        {
            SaleId = sale.Id,
            VenueId = venueId,
            AmountBaht = sale.TotalBaht,
            Method = Enum.Parse<PaymentMethod>(paidBy),
            ReceivedAt = now,
            ReceivedByUserId = membership.UserId,
        });

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "shop_sold {SaleId} {VenueId} {Lines} {TotalBaht} {Method}",
            sale.Id,
            venueId,
            sale.Lines.Count,
            sale.TotalBaht,
            paidBy);

        return TypedResults.Created(
            $"/api/venues/{venueId}/shop/sales",
            await OneSaleAsync(database, venueId, sale.Id, cancellationToken));
    }

    /// <summary>
    /// Takes a sale back (PRD US-32): the stock goes on the shelf again and the money going out is
    /// written down. Nothing is deleted — what happened happened, and the rows say so.
    /// </summary>
    private static async Task<Results<Ok<ShopSaleResponse>, ProblemHttpResult>> CancelSaleAsync(
        Guid venueId,
        Guid saleId,
        ShopSaleCancelRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();
        var now = timeProvider.GetUtcNow();

        if (BookingStatusChange.Recorded(request.Reason) is not { } reason)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SlipErrorCodes.ReasonTooLong);
        }

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        var sale = await database.ShopSales
            .AsNoTracking()
            .Include(one => one.Lines)
            .SingleOrDefaultAsync(
                one => one.Id == saleId && one.VenueId == venueId, cancellationToken);

        if (sale is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status404NotFound, ShopErrorCodes.SaleNotFound);
        }

        // Conditional on it still standing: two people taking one sale back would put the stock
        // on the shelf twice and record the money going out twice.
        var taken = await database.ShopSales
            .Where(one => one.Id == saleId && one.CancelledAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(one => one.CancelledAt, now)
                    .SetProperty(one => one.CancelledByUserId, membership.UserId)
                    .SetProperty(one => one.CancelReason, reason.Length == 0 ? null : reason),
                cancellationToken);

        if (taken == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, ShopErrorCodes.SaleAlreadyCancelled);
        }

        var counted = await database.ShopItems
            .AsNoTracking()
            .Where(one => one.VenueId == venueId && one.Counted)
            .Select(one => one.Id)
            .ToListAsync(cancellationToken);

        foreach (var line in sale.Lines.Where(line => counted.Contains(line.ItemId)))
        {
            database.StockEntries.Add(new StockEntry
            {
                ItemId = line.ItemId,
                Quantity = line.Quantity,
                Move = StockMove.GivenBack,
                SaleId = sale.Id,
                At = now,
                ByUserId = membership.UserId,
            });
        }

        // The money leaving is written down where money leaving is written down: an expense, so
        // the day's cash count is right whichever way the money went (PRD US-26, US-33).
        var handedBack = await database.PaymentReceipts
            .AsNoTracking()
            .Where(receipt => receipt.SaleId == saleId)
            .Select(receipt => new { receipt.AmountBaht, receipt.Method })
            .SingleAsync(cancellationToken);

        database.Spends.Add(new Spend
        {
            VenueId = venueId,
            Kind = SpendKind.Stock,
            AmountBaht = handedBack.AmountBaht,
            PaidOn = PlatformRequirements.BangkokDateAndHour(now).Date,
            PaidBy = handedBack.Method,
            Note = reason.Length == 0 ? null : reason,
            RecordedAt = now,
            RecordedByUserId = membership.UserId,
        });

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "shop_sale_cancelled {SaleId} {VenueId} {TotalBaht}",
            saleId,
            venueId,
            sale.TotalBaht);

        return TypedResults.Ok(
            await OneSaleAsync(database, venueId, saleId, cancellationToken));
    }

    /// <summary>What this venue paid out, newest first (PRD US-33).</summary>
    private static async Task<Ok<SpendResponse[]>> SpendingAsync(
        Guid venueId,
        DateOnly? from,
        DateOnly? to,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var today = PlatformRequirements.BangkokToday(timeProvider);
        var first = from ?? new DateOnly(today.Year, today.Month, 1);
        var last = to ?? first.AddMonths(1).AddDays(-1);

        return TypedResults.Ok(
            await database.Spends
                .AsNoTracking()
                .Where(one =>
                    one.VenueId == venueId && one.PaidOn >= first && one.PaidOn <= last)
                .OrderByDescending(one => one.PaidOn)
                .ThenByDescending(one => one.RecordedAt)
                .Select(one => new SpendResponse(
                    one.Id,
                    one.Kind.ToString(),
                    one.AmountBaht,
                    one.PaidOn,
                    one.PaidBy.ToString(),
                    one.Note,
                    one.VoidedAt,
                    one.VoidReason))
                .ToArrayAsync(cancellationToken));
    }

    /// <summary>
    /// Writes down money the venue paid out (PRD US-33). Buying stock is one of these that also
    /// puts something on the shelf — both rows in one write, because a venue that had to do it
    /// twice would sooner or later do it once.
    /// </summary>
    private static async Task<Results<Created<SpendResponse>, ProblemHttpResult>> SpendAsync(
        Guid venueId,
        SpendRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();
        var today = PlatformRequirements.BangkokToday(timeProvider);

        if (request.Kind is not { } named
            || !Enum.GetNames<SpendKind>().Contains(named, StringComparer.Ordinal))
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SpendErrorCodes.InvalidKind);
        }

        if (request.PaidBy is not { } paidBy
            || !Enum.GetNames<PaymentMethod>().Contains(paidBy, StringComparer.Ordinal))
        {
            return ApiProblem.Of(
                StatusCodes.Status400BadRequest, BookingErrorCodes.InvalidCounterPayment);
        }

        var amount = decimal.Round(request.AmountBaht ?? 0m, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0m)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SpendErrorCodes.InvalidAmount);
        }

        // Paid on a day, and not one that has not happened. Money is written down after it moves.
        var paidOn = request.PaidOn ?? today;
        if (paidOn > today)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SpendErrorCodes.InvalidDate);
        }

        if (request.Note is { Length: > Spend.NoteMaxLength })
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SpendErrorCodes.NoteTooLong);
        }

        var kind = Enum.Parse<SpendKind>(named);

        // Buying stock says what and how many; anything else says neither. Half of it is a row
        // that looks like a purchase and moves nothing.
        var buying = request.ItemId is not null || request.Quantity is not null;
        if (buying
            && (kind != SpendKind.Stock
                || request.ItemId is null
                || request.Quantity is not { } many
                || !Shop.IsAQuantity(many)))
        {
            return ApiProblem.Of(
                StatusCodes.Status400BadRequest, SpendErrorCodes.NotAStockPurchase);
        }

        var now = timeProvider.GetUtcNow();

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        ShopItem? item = null;
        if (request.ItemId is { } itemId)
        {
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({itemId.ToString()}, 0))",
                cancellationToken);

            item = await database.ShopItems
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    one => one.Id == itemId && one.VenueId == venueId && one.Counted,
                    cancellationToken);

            if (item is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ApiProblem.Of(StatusCodes.Status404NotFound, ShopErrorCodes.ItemUnknown);
            }
        }

        var spend = new Spend
        {
            VenueId = venueId,
            Kind = kind,
            AmountBaht = amount,
            PaidOn = paidOn,
            PaidBy = Enum.Parse<PaymentMethod>(paidBy),
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            RecordedAt = now,
            RecordedByUserId = membership.UserId,
        };

        database.Spends.Add(spend);

        if (item is not null)
        {
            database.StockEntries.Add(new StockEntry
            {
                ItemId = item.Id,
                Quantity = request.Quantity!.Value,
                Move = StockMove.BoughtIn,
                SpendId = spend.Id,
                At = now,
                ByUserId = membership.UserId,
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "venue_spent {SpendId} {VenueId} {Kind} {AmountBaht}",
            spend.Id,
            venueId,
            spend.Kind,
            spend.AmountBaht);

        return TypedResults.Created(
            $"/api/venues/{venueId}/shop/spending",
            Drawn(spend));
    }

    /// <summary>
    /// Takes one back (PRD US-33). The row stays — what the venue said at the time is part of the
    /// history — and stops counting, the same as voiding a record of money sent back (US-18).
    /// </summary>
    private static async Task<Results<Ok<SpendResponse>, ProblemHttpResult>> VoidSpendAsync(
        Guid venueId,
        Guid spendId,
        ShopSaleCancelRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();
        var now = timeProvider.GetUtcNow();

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > Spend.NoteMaxLength)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SpendErrorCodes.NoteTooLong);
        }

        var voided = await database.Spends
            .Where(one => one.Id == spendId && one.VenueId == venueId && one.VoidedAt == null)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(one => one.VoidedAt, now)
                    .SetProperty(one => one.VoidedByUserId, membership.UserId)
                    .SetProperty(one => one.VoidReason, reason),
                cancellationToken);

        var spend = await database.Spends
            .AsNoTracking()
            .SingleOrDefaultAsync(
                one => one.Id == spendId && one.VenueId == venueId, cancellationToken);

        if (spend is null)
        {
            return ApiProblem.Of(StatusCodes.Status404NotFound, SpendErrorCodes.NotFound);
        }

        return voided == 0
            ? ApiProblem.Of(StatusCodes.Status409Conflict, SpendErrorCodes.AlreadyVoided)
            : TypedResults.Ok(Drawn(spend));
    }

    /// <summary>
    /// Says the shelf holds a different number than the ledger does (PRD US-33). It is the one
    /// movement with no money beside it, and the only one that has to say why.
    /// </summary>
    private static async Task<Results<Ok<ShopItemResponse>, ProblemHttpResult>> CountAsync(
        Guid venueId,
        Guid itemId,
        StockCountRequest request,
        CurrentVenue venue,
        AppDbContext database,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var membership = venue.Require();
        var now = timeProvider.GetUtcNow();

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > StockEntry.ReasonMaxLength)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SpendErrorCodes.NoteTooLong);
        }

        if (request.Counted is not { } shelf || shelf < 0)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, ShopErrorCodes.NotASale);
        }

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({itemId.ToString()}, 0))",
            cancellationToken);

        var item = await database.ShopItems
            .AsNoTracking()
            .SingleOrDefaultAsync(
                one => one.Id == itemId
                    && one.VenueId == venueId
                    && one.Counted
                    && one.WithdrawnAt == null,
                cancellationToken);

        if (item is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status404NotFound, ShopErrorCodes.ItemUnknown);
        }

        var left = (await LeftAsync(database, [itemId], cancellationToken))
            .GetValueOrDefault(itemId);

        // A count that agrees with the ledger moves nothing, and a row of nothing says nothing.
        if (shelf != left)
        {
            database.StockEntries.Add(new StockEntry
            {
                ItemId = itemId,
                Quantity = shelf - left,
                Move = StockMove.Counted,
                Reason = reason,
                At = now,
                ByUserId = membership.UserId,
            });

            await database.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        if (shelf != left)
        {
            AppEvents.For(loggers).LogInformation(
                "stock_counted {ItemId} {VenueId} {By}", itemId, venueId, shelf - left);
        }

        return TypedResults.Ok(
            (await ItemsAsync(database, venueId, cancellationToken))
                .Single(one => one.ItemId == itemId));
    }

    /// <summary>How many of each of these there are: the sum of the movements, and nothing else.</summary>
    private static async Task<Dictionary<Guid, int>> LeftAsync(
        AppDbContext database,
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken) =>
        (await database.StockEntries
            .Where(entry => itemIds.Contains(entry.ItemId))
            .GroupBy(entry => entry.ItemId)
            .Select(entries => new { ItemId = entries.Key, Left = entries.Sum(one => one.Quantity) })
            .ToListAsync(cancellationToken))
        .ToDictionary(one => one.ItemId, one => one.Left);

    private static async Task<ShopSaleResponse> OneSaleAsync(
        AppDbContext database,
        Guid venueId,
        Guid saleId,
        CancellationToken cancellationToken)
    {
        var sale = await database.ShopSales
            .AsNoTracking()
            .Include(one => one.Lines)
            .SingleAsync(one => one.Id == saleId && one.VenueId == venueId, cancellationToken);

        return new ShopSaleResponse(
            sale.Id,
            sale.BookingId,
            sale.TotalBaht,
            sale.SoldAt,
            sale.CancelledAt,
            sale.CancelReason,
            [
                .. sale.Lines
                    .OrderBy(line => line.Name)
                    .Select(line => new ShopSaleLineResponse(
                        line.ItemId, line.Name, line.Quantity, line.EachBaht)),
            ]);
    }

    private static SpendResponse Drawn(Spend spend) =>
        new(
            spend.Id,
            spend.Kind.ToString(),
            spend.AmountBaht,
            spend.PaidOn,
            spend.PaidBy.ToString(),
            spend.Note,
            spend.VoidedAt,
            spend.VoidReason);
}
