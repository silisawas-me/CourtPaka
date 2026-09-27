using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// How much of the venue's money one person may say has gone back, in a single record
/// (PRD US-18).
///
/// One place, because the number the desk is shown before it types an amount and the number the
/// endpoint refuses on have to be the same number. There is no way past it and deliberately no
/// override: the PRD says so in as many words — a code that can be borrowed is not an approval.
/// Somebody who cannot send this much hands the booker to somebody who can.
/// </summary>
public static class RefundLimits
{
    /// <summary>
    /// Whether this person may write down this amount, or null where they may. An owner always
    /// may; everybody else is held to what the owner set them, which starts at nothing.
    /// </summary>
    public static bool Allows(VenueMembership membership, decimal amountBaht) =>
        membership.RefundCeiling is not { } ceiling || amountBaht <= ceiling;

    /// <summary>
    /// The largest limit an owner may set. Not a rule about trust — it is the column's own
    /// bound, so a number that could never be a refund cannot be stored as one.
    /// </summary>
    public const decimal Most = 1_000_000m;

    /// <summary>Whether a number an owner typed could be a limit at all.</summary>
    public static bool IsALimit(decimal baht) =>
        baht >= 0m && baht <= Most && decimal.Round(baht, 2, MidpointRounding.AwayFromZero) == baht;
}
