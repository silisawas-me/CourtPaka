using CourtBooking.Api.Identity;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Bookings;

/// <summary>Why something moved in or out of a venue's stock (PRD US-32, US-33).</summary>
public enum StockMove
{
    /// <summary>Bought in. The buying side of it is an expense (PRD US-33).</summary>
    BoughtIn = 1,

    /// <summary>Sold across the counter.</summary>
    Sold = 2,

    /// <summary>Came back because the sale was cancelled.</summary>
    GivenBack = 3,

    /// <summary>
    /// Counted, and found to be a different number. The only movement with no money beside it,
    /// and the only one that needs a reason written by hand.
    /// </summary>
    Counted = 4,
}

/// <summary>What a venue spent money on (PRD US-33). Its own list, because the answer is a report.</summary>
public enum SpendKind
{
    /// <summary>Stock to sell: shuttlecocks, water, grips.</summary>
    Stock = 1,

    /// <summary>What the building costs to run.</summary>
    Utilities = 2,

    /// <summary>People.</summary>
    Wages = 3,

    /// <summary>Putting something right.</summary>
    Repairs = 4,

    Other = 5,
}

/// <summary>
/// Something a venue sells across the counter (PRD US-32): shuttlecocks, water, an hour of a
/// racquet. A price on a board, and like every other price here it is never edited — a venue
/// changes what it charges by taking the line off and putting a new one up, so a sale already
/// made still points at what it was sold at (BR-05).
/// </summary>
public sealed class ShopItem
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    public required string Name { get; init; }

    public required decimal PriceBaht { get; init; }

    /// <summary>What one of them is, in the venue's own words: a tube, a bottle, an hour.</summary>
    public required string Unit { get; init; }

    /// <summary>
    /// Whether there is a number of them. A racquet to hire is not counted — the venue knows how
    /// many it has and nobody wants a ledger of it — and shuttlecocks are (PRD US-33).
    /// </summary>
    public required bool Counted { get; init; }

    /// <summary>Below this, somebody is told there are not many left. Null for what is not counted.</summary>
    public int? TellMeAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required Guid CreatedByUserId { get; init; }

    /// <summary>When it came off the board, if it has. One way: an item is replaced, not restored.</summary>
    public DateTimeOffset? WithdrawnAt { get; set; }

    public Guid? WithdrawnByUserId { get; set; }

    public const int NameMaxLength = 100;
    public const int UnitMaxLength = 30;

    public Venue? Venue { get; init; }
}

/// <summary>
/// One trip to the counter (PRD US-32). It may be beside a booking — the group that bought a tube
/// halfway through their hour — or on its own, because somebody walked in for shuttlecocks and
/// never played.
///
/// What it came to is the sum of its lines, each priced when it was rung up.
/// </summary>
public sealed class ShopSale
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    /// <summary>The booking it was rung up against, or null for somebody who only bought.</summary>
    public Guid? BookingId { get; init; }

    /// <summary>
    /// What it came to: the sum of its lines, each at the price it was rung up at. Stored so a
    /// report never has to join to work out a total, and set from the lines rather than beside
    /// them so the receipt cannot fail to add up to what was charged.
    /// </summary>
    public decimal TotalBaht { get; private set; }

    /// <summary>The lines are the sale. Everything else about it is bookkeeping.</summary>
    public ShopSale Selling(IEnumerable<ShopSaleLine> lines)
    {
        Lines.AddRange(lines);
        TotalBaht = Lines.Sum(line => line.Baht);
        return this;
    }

    public required DateTimeOffset SoldAt { get; init; }

    public required Guid SoldByUserId { get; init; }

    /// <summary>When it was taken back, if it was. One way, and the stock goes back with it.</summary>
    public DateTimeOffset? CancelledAt { get; set; }

    public Guid? CancelledByUserId { get; set; }

    public string? CancelReason { get; set; }

    public List<ShopSaleLine> Lines { get; init; } = [];

    public Venue? Venue { get; init; }

    public Booking? Booking { get; init; }
}

/// <summary>One line of a sale, at what it cost when it was rung up (PRD US-32, BR-05).</summary>
public sealed class ShopSaleLine
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid SaleId { get; init; }

    public required Guid ItemId { get; init; }

    /// <summary>Copied in, so the row still reads right when the board has changed.</summary>
    public required string Name { get; init; }

    public required int Quantity { get; init; }

    public required decimal EachBaht { get; init; }

    public decimal Baht => EachBaht * Quantity;

    public ShopSale? Sale { get; init; }

    public ShopItem? Item { get; init; }
}

/// <summary>
/// One movement of stock, in or out (PRD US-32, US-33). Rows are only ever added — the database
/// refuses anything else — so what a venue has is the sum of what happened to it, and "where did
/// the shuttlecocks go" is a list rather than a number somebody keeps.
/// </summary>
public sealed class StockEntry
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid ItemId { get; init; }

    /// <summary>Positive going in, negative going out. Never zero.</summary>
    public required int Quantity { get; init; }

    public required StockMove Move { get; init; }

    /// <summary>The sale it went out on, or came back from.</summary>
    public Guid? SaleId { get; init; }

    /// <summary>The expense it was bought on (PRD US-33).</summary>
    public Guid? SpendId { get; init; }

    /// <summary>Why, in the venue's own words. Only a count needs one.</summary>
    public string? Reason { get; init; }

    public required DateTimeOffset At { get; init; }

    public required Guid ByUserId { get; init; }

    public const int ReasonMaxLength = 200;

    public ShopItem? Item { get; init; }

    public ShopSale? Sale { get; init; }

    public Spend? Spend { get; init; }

    public AppUser? By { get; init; }
}

/// <summary>
/// Money the venue paid out (PRD US-33). Added and never edited, the same as a record of money
/// sent back (US-18): a record that can be rewritten is not a record. The one thing that can
/// happen to it afterwards is being voided, once, with a reason.
/// </summary>
public sealed class Spend
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    public required SpendKind Kind { get; init; }

    public required decimal AmountBaht { get; init; }

    /// <summary>The venue's own day it was paid on (BR-10), which is the day it counts against.</summary>
    public required DateOnly PaidOn { get; init; }

    /// <summary>
    /// How it left. Cash is the only one that empties the drawer, and the count at the end of the
    /// day has to know about it or the till never balances (PRD US-26).
    /// </summary>
    public required PaymentMethod PaidBy { get; init; }

    public string? Note { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    public required Guid RecordedByUserId { get; init; }

    public DateTimeOffset? VoidedAt { get; set; }

    public Guid? VoidedByUserId { get; set; }

    public string? VoidReason { get; set; }

    public const int NoteMaxLength = 400;

    public Venue? Venue { get; init; }

    public AppUser? RecordedBy { get; init; }
}

/// <summary>
/// What a venue's counter will sell and what it will not, in one place — so the screen that rings
/// it up, the endpoint that writes it and the report that counts it all agree (PRD US-32, US-33).
/// </summary>
public static class Shop
{
    /// <summary>The most of one thing anybody sells in a single trip to the counter.</summary>
    public const int MostOfOneThing = 100;

    /// <summary>The most lines one sale may have. A counter is not a supermarket.</summary>
    public const int MostLines = 20;

    /// <summary>How many of the sales that are over a venue is shown.</summary>

    /// <summary>Why this cannot go on the board, or null.</summary>
    public static string? Refusal(string? name, string? unit, decimal priceBaht, int? tellMeAt)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > ShopItem.NameMaxLength)
        {
            return ShopErrorCodes.InvalidName;
        }

        if (string.IsNullOrWhiteSpace(unit) || unit.Trim().Length > ShopItem.UnitMaxLength)
        {
            return ShopErrorCodes.InvalidUnit;
        }

        // Nothing free: a line with no price is something given away, which is a decision rather
        // than a thing to sell, and it would make a sale of nothing.
        if (priceBaht <= 0m || priceBaht != decimal.Round(priceBaht, 2))
        {
            return ShopErrorCodes.InvalidPrice;
        }

        return tellMeAt is < 0 ? ShopErrorCodes.InvalidTellMeAt : null;
    }

    /// <summary>Whether a line is one somebody could actually be handed.</summary>
    public static bool IsAQuantity(int quantity) => quantity is > 0 and <= MostOfOneThing;

    /// <summary>
    /// What somebody wrote to explain a thing they did, or null if they wrote nothing usable.
    /// One answer, because the three doors that insist on a reason — voiding an expense, taking a
    /// sale back, saying the shelf holds a different number — insist on the same thing.
    /// </summary>
    public static string? Said(string? text, int most)
    {
        var said = text?.Trim();
        return string.IsNullOrEmpty(said) || said.Length > most ? null : said;
    }

    /// <summary>Whether there are few enough left to say so (PRD US-33).</summary>
    public static bool RunningLow(bool counted, int left, int? tellMeAt) =>
        counted && tellMeAt is { } floor && left <= floor;
}

public static class ShopErrorCodes
{
    public const string InvalidName = "shop.invalid_name";
    public const string InvalidUnit = "shop.invalid_unit";
    public const string InvalidPrice = "shop.invalid_price";
    public const string InvalidTellMeAt = "shop.invalid_tell_me_at";

    /// <summary>No item of this venue has that id, or it has been taken off the board.</summary>
    public const string ItemUnknown = "shop.item_unknown";

    public const string ItemAlreadyWithdrawn = "shop.item_already_withdrawn";

    /// <summary>Nothing was rung up, or too many lines, or a line of nothing.</summary>
    public const string NotASale = "shop.not_a_sale";

    /// <summary>Fewer of them left than the counter is trying to hand over (PRD US-32).</summary>
    public const string NotEnoughStock = "shop.not_enough_stock";

    public const string SaleNotFound = "shop.sale_not_found";
    public const string SaleAlreadyCancelled = "shop.sale_already_cancelled";

    /// <summary>The booking it would be rung up against is not one of this venue's.</summary>
    public const string BookingUnknown = "shop.booking_unknown";

    /// <summary>Nothing was written to say why, or too much was (PRD US-32, US-33).</summary>
    public const string ReasonNeeded = "shop.reason_needed";

    /// <summary>A shelf cannot hold fewer than none of something (PRD US-33).</summary>
    public const string InvalidCount = "shop.invalid_count";
}

public static class SpendErrorCodes
{
    public const string InvalidAmount = "spend.invalid_amount";
    public const string InvalidKind = "spend.invalid_kind";
    public const string InvalidDate = "spend.invalid_date";
    public const string NoteTooLong = "spend.note_too_long";

    /// <summary>Nothing was written to say why it is being taken back (PRD US-33).</summary>
    public const string ReasonNeeded = "spend.reason_needed";
    public const string NotFound = "spend.not_found";
    public const string AlreadyVoided = "spend.already_voided";

    /// <summary>Buying stock says which item and how many; anything else says neither.</summary>
    public const string NotAStockPurchase = "spend.not_a_stock_purchase";
}
