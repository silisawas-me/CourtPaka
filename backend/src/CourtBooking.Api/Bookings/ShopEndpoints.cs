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

        // Selling is selling, which is what a suspension stops.
        shop.MapPost("/sales", SellAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageBookings));
        shop.MapPost("/sales/{saleId:guid}/cancel", CancelSaleAsync);

        // Counting the shelf is not selling, so it stays open at a suspended venue.
        shop.MapPost("/items/{itemId:guid}/count", CountAsync);

        // The board is a setting, like a price list (PRD US-14).
        shop.MapPost("/items", AddItemAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageSettings));
        shop.MapPost("/items/{itemId:guid}/withdraw", WithdrawItemAsync)
            .RequireAuthorization(VenuePolicies.Needs(VenuePermissions.ManageSettings));

        MapSpendingEndpoints(venue);
    }

    /// <summary>
    /// What the venue paid out (PRD US-33). Its own door, not the shop's: four of the five kinds
    /// — the water bill, wages, a repair — have nothing to do with what the counter sells, and
    /// reading them is a report while writing one is a shift's work. So the AC gives the two
    /// halves different permissions, and a group cannot answer for both.
    /// </summary>
    private static void MapSpendingEndpoints(RouteGroupBuilder venue)
    {
        // Two halves with two permissions, so they are two groups. A policy declared on a group
        // and another on the endpoint inside it are *both* required — writing under the group's
        // permission and reading under a second one would mean nobody could do either half
        // without holding the other, which is the opposite of splitting them (PRD US-33).
        var writing = venue.MapGroup("/spending");
        var reading = venue.MapGroup("/spending");

        // Neither half is selling, so a suspension stops neither: money that has already left has
        // to be accounted for, and the bills keep arriving (PRD US-20).
        writing.RequireAuthorization(
            VenuePolicies.NeedsEvenWhenSuspended(VenuePermissions.ManageBookings));

        // Reading it is a report. The person who pays the water bill at the counter and the person
        // who reads what the venue spent this month are not the same person.
        reading.RequireAuthorization(
            VenuePolicies.NeedsEvenWhenSuspended(VenuePermissions.ViewReports));

        reading.MapGet("/", SpendingAsync);

        writing.MapPost("/", SpendAsync);
        writing.MapPost("/{spendId:guid}/void", VoidSpendAsync);
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
        CancellationToken cancellationToken) =>
        [
            .. (await OnTheShelfAsync(database, venueId, null, cancellationToken))
                .OrderBy(one => one.Item.WithdrawnAt != null)
                .ThenBy(one => one.Item.Name, StringComparer.Ordinal)
                .Select(one => Drawn(one.Item, one.Left)),
        ];

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

        // Nothing can be on the shelf of a thing that did not exist a moment ago, so the board
        // does not need reading to say so.
        return TypedResults.Created($"/api/venues/{venueId}/shop/items", Drawn(item, 0));
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

        var drawn = await OneItemAsync(database, venueId, itemId, cancellationToken);

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

        // The till's day, not midnight to midnight: a tube sold after the count belongs to the
        // same day as the money it was paid with, or the shop's book and the drawer's disagree
        // and nobody can say which is right (PRD US-26, US-32).
        var (from, until) = await Takings.TillDayAsync(database, venueId, day, cancellationToken);

        var sales = await database.ShopSales
            .AsNoTracking()
            .Include(one => one.Lines)
            .Where(one => one.VenueId == venueId && one.SoldAt >= from && one.SoldAt < until)
            .OrderByDescending(one => one.SoldAt)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok<ShopSaleResponse[]>([.. sales.Select(Drawn)]);
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

        // Queued on every line before any of them is read: two counters selling the last tube at
        // the same moment would otherwise both read one left.
        await Locks.OnAsync(database, wanted, cancellationToken);

        var shelf = await OnTheShelfAsync(database, venueId, wanted, cancellationToken);

        if (shelf.Count != asked.Length || shelf.Any(one => one.Item.WithdrawnAt is not null))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status404NotFound, ShopErrorCodes.ItemUnknown);
        }

        var items = shelf.Select(one => one.Item).ToList();
        var left = shelf.ToDictionary(one => one.Item.Id, one => one.Left);

        var sale = new ShopSale
        {
            VenueId = venueId,
            BookingId = request.BookingId,
            SoldAt = now,
            SoldByUserId = membership.UserId,
        };

        var board = items.ToDictionary(one => one.Id);
        var lines = new List<ShopSaleLine>(asked.Length);

        foreach (var line in asked)
        {
            var item = board[line.ItemId];

            // Nothing is sold that is not there. What is not counted has no number to be short
            // of — a racquet to hire is the venue's own business (PRD US-32).
            if (item.Counted && left.GetValueOrDefault(item.Id) < line.Quantity)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ApiProblem.Of(
                    StatusCodes.Status409Conflict, ShopErrorCodes.NotEnoughStock);
            }

            lines.Add(new ShopSaleLine
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

        database.ShopSales.Add(sale.Selling(lines));

        // The money is in the venue's hands as the line is rung up, so the day's count is told
        // about it the same way every other kind of money is (PRD US-26).
        database.PaymentReceipts.Add(new PaymentReceipt
        {
            SaleId = sale.Id,
            VenueId = venueId,
            CountsOn = await CounterMoneyEndpoints.CountsOnAsync(
                database, venueId, now, cancellationToken),
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

        return TypedResults.Created($"/api/venues/{venueId}/shop/sales", Drawn(sale));
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

        if (Shop.Said(request.Reason, BookingStatusChange.ReasonMaxLength) is not { } reason)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, ShopErrorCodes.ReasonNeeded);
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
                    .SetProperty(one => one.CancelReason, reason),
                cancellationToken);

        if (taken == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(
                StatusCodes.Status409Conflict, ShopErrorCodes.SaleAlreadyCancelled);
        }

        // Only what has a number goes back on a shelf, and the line's own row says which those
        // are — asking the whole board would read every item the venue sells to classify three.
        var goingBack = await database.ShopSaleLines
            .AsNoTracking()
            .Where(line => line.SaleId == saleId && line.Item!.Counted)
            .Select(line => new { line.ItemId, line.Quantity })
            .ToListAsync(cancellationToken);

        // Putting stock back is writing the ledger, so it queues where every other writer of it
        // queues. Without this a count running at the same moment reads the shelf before these
        // rows and writes its adjustment after them, and the count — which is supposed to be the
        // last word on what is there — ends up wrong by what came back.
        await Locks.OnAsync(
            database, goingBack.Select(line => line.ItemId), cancellationToken);

        foreach (var line in goingBack)
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

        // The money handed back needs no row of its own: this cancellation and the receipt the
        // sale was paid with say it, and the drawer reads them (Takings, PRD US-26). Writing it as
        // an expense would file a customer's refund under what the venue bought, and the day's
        // figures would lose the sale twice — once by not counting it, once by paying for it.
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AppEvents.For(loggers).LogInformation(
            "shop_sale_cancelled {SaleId} {VenueId} {TotalBaht}",
            saleId,
            venueId,
            sale.TotalBaht);

        sale.CancelledAt = now;
        sale.CancelReason = reason;

        return TypedResults.Ok(Drawn(sale));
    }

    /// <summary>What this venue paid out, newest first (PRD US-33).</summary>
    private static async Task<Results<Ok<SpendResponse[]>, ProblemHttpResult>> SpendingAsync(
        Guid venueId,
        DateOnly? from,
        DateOnly? to,
        AppDbContext database,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var (first, last) = PlatformRequirements.MonthOr(from, to, timeProvider);

        if (last < first || last.DayNumber - first.DayNumber + 1 > VenueDashboard.MaxDays)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, DashboardErrorCodes.InvalidRange);
        }

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
            await Locks.OnAsync(database, itemId, cancellationToken);

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
            $"/api/venues/{venueId}/spending",
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

        if (Shop.Said(request.Reason, Spend.NoteMaxLength) is not { } reason)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, SpendErrorCodes.ReasonNeeded);
        }

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        // What this expense put on a shelf, if it put anything there. Read before the row is
        // stamped, and locked first, because taking it back writes the ledger like any other
        // writer does.
        var delivered = await database.StockEntries
            .AsNoTracking()
            .Where(entry => entry.SpendId == spendId && entry.Spend!.VenueId == venueId)
            .Select(entry => new { entry.ItemId, entry.Quantity })
            .ToListAsync(cancellationToken);

        await Locks.OnAsync(
            database, delivered.Select(entry => entry.ItemId), cancellationToken);

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
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status404NotFound, SpendErrorCodes.NotFound);
        }

        if (voided == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status409Conflict, SpendErrorCodes.AlreadyVoided);
        }

        // An expense that filled a shelf empties it again when it is taken back. The way a venue
        // corrects a delivery it keyed wrong is to void it and enter it again — so leaving the
        // goods there would put them on the shelf twice, and the ledger is append-only, which
        // means the only way back from that is a stocktake.
        foreach (var entry in delivered)
        {
            database.StockEntries.Add(new StockEntry
            {
                ItemId = entry.ItemId,
                Quantity = -entry.Quantity,
                Move = StockMove.BoughtIn,
                SpendId = spendId,
                At = now,
                ByUserId = membership.UserId,
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Ok(Drawn(spend));
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

        if (Shop.Said(request.Reason, StockEntry.ReasonMaxLength) is not { } reason)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, ShopErrorCodes.ReasonNeeded);
        }

        if (request.Counted is not { } shelf || shelf < 0)
        {
            return ApiProblem.Of(StatusCodes.Status400BadRequest, ShopErrorCodes.InvalidCount);
        }

        await using var transaction =
            await database.Database.BeginTransactionAsync(cancellationToken);

        await Locks.OnAsync(database, itemId, cancellationToken);

        var found = (await OnTheShelfAsync(database, venueId, [itemId], cancellationToken))
            .SingleOrDefault(one =>
                one.Item.Counted && one.Item.WithdrawnAt == null);

        if (found.Item is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ApiProblem.Of(StatusCodes.Status404NotFound, ShopErrorCodes.ItemUnknown);
        }

        // A count that agrees with the ledger moves nothing, and a row of nothing says nothing.
        var by = shelf - found.Left;

        if (by != 0)
        {
            database.StockEntries.Add(new StockEntry
            {
                ItemId = itemId,
                Quantity = by,
                Move = StockMove.Counted,
                Reason = reason,
                At = now,
                ByUserId = membership.UserId,
            });

            await database.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        if (by != 0)
        {
            AppEvents.For(loggers).LogInformation(
                "stock_counted {ItemId} {VenueId} {By}", itemId, venueId, by);
        }

        return TypedResults.Ok(Drawn(found.Item, shelf));
    }

    /// <summary>
    /// The venue's things and how many of each are there: one row per item, the balance being the
    /// sum of its movements and nothing else. The one place that answers "how many are left", so
    /// that a rule about which movements count cannot be true of one screen and not another.
    /// </summary>
    /// <param name="itemIds">Only these, or every one of the venue's when null.</param>
    private static async Task<List<(ShopItem Item, int Left)>> OnTheShelfAsync(
        AppDbContext database,
        Guid venueId,
        IReadOnlyCollection<Guid>? itemIds,
        CancellationToken cancellationToken) =>
        [
            .. (await database.ShopItems
                .AsNoTracking()
                .Where(one =>
                    one.VenueId == venueId
                    && (itemIds == null || itemIds.Contains(one.Id)))
                .Select(one => new
                {
                    Item = one,
                    Left = database.StockEntries
                        .Where(entry => entry.ItemId == one.Id)
                        .Sum(entry => (int?)entry.Quantity) ?? 0,
                })
                .ToListAsync(cancellationToken))
                .Select(one => (one.Item, one.Left)),
        ];

    /// <summary>One line of the board, or nothing when this venue has no such line.</summary>
    private static async Task<ShopItemResponse?> OneItemAsync(
        AppDbContext database,
        Guid venueId,
        Guid itemId,
        CancellationToken cancellationToken) =>
        (await OnTheShelfAsync(database, venueId, [itemId], cancellationToken))
            .Select(one => Drawn(one.Item, one.Left))
            .SingleOrDefault();

    private static ShopItemResponse Drawn(ShopItem item, int left) =>
        new(
            item.Id,
            item.Name,
            item.PriceBaht,
            item.Unit,
            item.Counted,
            item.TellMeAt,

            // Nothing for what is not counted: a racquet to hire has no number, and a zero beside
            // it would read as none left (PRD US-32).
            item.Counted ? left : null,
            Shop.RunningLow(item.Counted, left, item.TellMeAt),
            item.WithdrawnAt);

    /// <summary>
    /// One trip to the counter as a screen reads it. Shaped from the rows in hand: a sale that was
    /// just written or just taken back is already known down to its lines, and asking the database
    /// to say it back is a round trip that can only agree.
    /// </summary>
    private static ShopSaleResponse Drawn(ShopSale sale) =>
        new(
            sale.Id,
            sale.BookingId,
            sale.TotalBaht,
            sale.SoldAt,
            sale.CancelledAt,
            sale.CancelReason,
            [
                .. sale.Lines
                    .OrderBy(line => line.Name, StringComparer.Ordinal)
                    .Select(line => new ShopSaleLineResponse(
                        line.ItemId, line.Name, line.Quantity, line.EachBaht)),
            ]);

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
