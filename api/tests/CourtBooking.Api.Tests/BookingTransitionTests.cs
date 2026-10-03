using CourtBooking.Api.Bookings;

namespace CourtBooking.Api.Tests;

/// <summary>The table transcribed from PRD 6.1, and the names PRD 8 gives its events.</summary>
public sealed class BookingTransitionTests
{
    [Theory]
    [InlineData(BookingStatus.Held, BookingStatus.PendingVerification)]
    [InlineData(BookingStatus.Held, BookingStatus.Expired)]
    [InlineData(BookingStatus.PendingVerification, BookingStatus.PendingVerification)]
    public void A_move_the_state_machine_allows_goes_through(BookingStatus from, BookingStatus to)
    {
        var recorded = BookingTransitions.Record(Guid.CreateVersion7(), from, to, Actor, At);

        Assert.Equal(from, recorded.From);
        Assert.Equal(to, recorded.To);
        Assert.Equal(Actor, recorded.ChangedByUserId);
        Assert.Equal(At, recorded.ChangedAt);
    }

    [Fact]
    public void A_booking_records_coming_into_existence()
    {
        var booking = Held();

        var first = Assert.Single(booking.StatusChanges);
        Assert.Null(first.From);
        Assert.Equal(BookingStatus.Held, first.To);
        Assert.Equal(booking.BookerUserId, first.ChangedByUserId);
    }



    [Theory]
    // Confirming is the venue's move, and it needs a slip checked first (US-12).
    [InlineData(BookingStatus.Held, BookingStatus.Confirmed)]
    // A hold that has run out is final; the booker starts again (PRD 6.1).
    [InlineData(BookingStatus.Expired, BookingStatus.PendingVerification)]
    // Confirmed may never become Rejected — the venue cancels with a reason instead (PRD 6.1).
    [InlineData(BookingStatus.Confirmed, BookingStatus.Rejected)]
    public void A_move_it_does_not_is_a_bug_not_a_refusal(BookingStatus from, BookingStatus to)
    {
        var wrong = Assert.Throws<InvalidOperationException>(
            () => BookingTransitions.Record(Guid.CreateVersion7(), from, to, Actor, At));

        Assert.Contains("PRD 6.1", wrong.Message);
    }

    [Theory]
    [InlineData(BookingStatus.PendingVerification, "booking_pending_verification")]
    [InlineData(BookingStatus.Expired, "booking_expired")]
    [InlineData(BookingStatus.NoShow, "booking_no_show")]
    [InlineData(BookingStatus.Held, "booking_held")]
    public void Every_status_has_the_event_name_the_measurements_expect(
        BookingStatus status,
        string expected) =>
        Assert.Equal(expected, BookingTransitions.EventName(status));

    /// <summary>
    /// A booking confirmed after its hours were played is played. Both the slip queue (US-12) and
    /// the money taken at the desk (US-26) need the answer before they write anything down.
    /// </summary>
    [Fact]
    public void Hours_already_behind_it_make_a_confirmation_a_completion()
    {
        var over = At.AddHours(-1);
        var ahead = At.AddHours(1);

        Assert.Equal(
            BookingStatus.Completed,
            BookingTransitions.LandingFor(BookingStatus.Confirmed, over, At));
        Assert.Equal(
            BookingStatus.Confirmed,
            BookingTransitions.LandingFor(BookingStatus.Confirmed, ahead, At));

        // Nothing else is carried further by the clock: a rejection is a rejection whenever it
        // was made.
        Assert.Equal(
            BookingStatus.Rejected,
            BookingTransitions.LandingFor(BookingStatus.Rejected, over, At));
    }

    /// <summary>
    /// The step the clock made is recorded beside the one somebody pressed, with no actor against
    /// it — a history that holds the first without the second has a hole in it (PRD 6.1).
    /// </summary>
    [Fact]
    public void A_move_the_clock_carried_further_is_two_rows_not_one()
    {
        var bookingId = Guid.CreateVersion7();

        var one = BookingTransitions.RecordsFor(
            bookingId,
            BookingStatus.PendingVerification,
            BookingStatus.Confirmed,
            BookingStatus.Confirmed,
            Actor,
            At);

        Assert.Single(one);
        Assert.Equal(Actor, one[0].ChangedByUserId);

        var two = BookingTransitions.RecordsFor(
            bookingId,
            BookingStatus.PendingVerification,
            BookingStatus.Confirmed,
            BookingStatus.Completed,
            Actor,
            At,
            "money arrived");

        Assert.Equal(2, two.Count);
        Assert.Equal("money arrived", two[0].Reason);
        Assert.Equal(BookingStatus.Confirmed, two[1].From);
        Assert.Equal(BookingStatus.Completed, two[1].To);
        Assert.Null(two[1].ChangedByUserId);
        // The chain is what says which came first; both rows carry the same moment.
        Assert.Equal(two[0].To, two[1].From);
        Assert.Equal(two[0].ChangedAt, two[1].ChangedAt);
    }

    private static readonly Guid Actor = Guid.CreateVersion7();

    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;

    private static Booking Held() =>
        Booking.Hold(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            [new SlotPrice(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddDays(1), 200m)],
            Deposit.Everything,
            DepositReason.VenueTerms,
            DateTimeOffset.UtcNow);
}
