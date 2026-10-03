using CourtBooking.Api.Bookings;

namespace CourtBooking.Api.Tests;

/// <summary>
/// Which bookings the counter may still take money for (PRD US-26). The rule decides both the
/// door on the screen and the endpoint behind it, so it is worth stating on its own.
/// </summary>
public sealed class TakingsTests
{
    [Fact]
    public void Money_is_taken_for_bookings_the_venue_will_honour_or_has_played()
    {
        foreach (var status in Takings.StillOwing)
        {
            Assert.True(Takings.CanTake(status, PaymentState.NotReceived, 500m));
        }
    }

    /// <summary>
    /// A hold has two ways out and neither is the till (PRD 6.1), and a booking that was turned
    /// away owes nothing forwards — money against it is a refund question wearing the wrong hat.
    /// </summary>
    [Theory]
    [InlineData(BookingStatus.Held)]
    [InlineData(BookingStatus.Expired)]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Rejected)]
    public void No_money_is_taken_where_nothing_is_owed_forwards(BookingStatus status)
    {
        Assert.False(Takings.CanTake(status, PaymentState.NotReceived, 500m));
    }

    /// <summary>
    /// A venue that already says it has the money is not asked for it again. Some bookings reach
    /// that answer with no receipt behind it — a slip accepted before receipts were written down,
    /// a venue answering for the money after the fact (PRD US-13) — and a door there is a door
    /// that collects twice.
    /// </summary>
    [Fact]
    public void A_booking_the_venue_calls_paid_is_not_asked_for_money_again()
    {
        Assert.False(Takings.CanTake(BookingStatus.Confirmed, PaymentState.Received, 500m));
        Assert.False(Takings.CanTake(BookingStatus.Completed, PaymentState.Received, 500m));
    }

    [Fact]
    public void Nothing_owed_is_nothing_to_take()
    {
        Assert.False(Takings.CanTake(BookingStatus.Confirmed, PaymentState.NotReceived, 0m));
    }

    /// <summary>
    /// What a count is out by points at rows of exactly that amount, in either direction — short
    /// by 300 and a 300 taken in cash are the same coincidence as over by 300 and a 300 still
    /// owed. Nothing looser: a list of roughly-right amounts is a list nobody reads (PRD US-26).
    /// </summary>
    [Theory]
    [InlineData(300, -300, true)]
    [InlineData(300, 300, true)]
    [InlineData(300.004, -300, true)]
    [InlineData(300, -299.99, false)]
    [InlineData(300, -600, false)]
    public void Only_an_amount_that_is_exactly_the_difference_explains_it(
        decimal amount,
        decimal difference,
        bool explains) =>
        Assert.Equal(explains, Takings.Explains(amount, difference));

    /// <summary>A till that balanced has nothing to explain, whatever is lying around.</summary>
    [Fact]
    public void A_count_that_came_out_even_is_explained_by_nothing()
    {
        Assert.False(Takings.Explains(0m, 0m));
        Assert.False(Takings.Explains(300m, 0m));
    }

    /// <summary>Paying the last of it answers only the booking that was waiting for an answer.</summary>
    [Fact]
    public void Only_a_booking_waiting_on_a_slip_is_confirmed_by_the_money_arriving()
    {
        Assert.True(Takings.PayingInFullConfirms(BookingStatus.PendingVerification));
        Assert.False(Takings.PayingInFullConfirms(BookingStatus.Confirmed));
        Assert.False(Takings.PayingInFullConfirms(BookingStatus.NoShow));
        Assert.False(Takings.PayingInFullConfirms(BookingStatus.Completed));
    }
}
