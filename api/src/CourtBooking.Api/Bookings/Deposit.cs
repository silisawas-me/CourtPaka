namespace CourtBooking.Api.Bookings;

/// <summary>
/// How much of a booking's price has to arrive before the hours are held (PRD US-28).
///
/// One place, because three screens ask it: the page that draws the QR, the queue that accepts
/// the slip, and the counter that takes what is left. A venue that asks for all of it is the way
/// the system has always worked, and is still the default — what this adds is the ability to ask
/// for less and let the rest be paid at the desk.
/// </summary>
public static class Deposit
{
    /// <summary>The whole price. A venue that says nothing asks for this, as it always has.</summary>
    public const int Everything = 100;

    /// <summary>
    /// The least a venue may ask for. Not zero: hours held for nothing are hours anybody can
    /// hold, and the 15 minutes a hold lasts (BR-02) is the only thing that would end it.
    /// </summary>
    public const int Least = 10;

    /// <summary>Whether a share is one a venue may ask for.</summary>
    public static bool IsAShare(int percent) => percent >= Least && percent <= Everything;

    /// <summary>
    /// What a share of a price comes to, to the satang, rounded away from zero — the venue asked
    /// for a share and the half satang is the venue's, since the rest follows at the desk.
    ///
    /// The whole price is answered exactly rather than computed, so that a booking a venue wants
    /// paid in full never ends a satang short of itself.
    /// </summary>
    public static decimal Of(decimal totalBaht, int percent) =>
        percent >= Everything
            ? totalBaht
            : Math.Round(totalBaht * percent / 100m, 2, MidpointRounding.AwayFromZero);
}
