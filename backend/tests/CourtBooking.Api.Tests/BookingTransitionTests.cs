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
        var booking = Held();
        booking.Status = from;

        booking.MoveTo(to, Actor, At);

        Assert.Equal(to, booking.Status);

        // The move and its record travel together (PRD 6.1).
        var recorded = booking.StatusChanges[^1];
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

    [Fact]
    public void A_refused_move_records_nothing()
    {
        var booking = Held();
        var before = booking.StatusChanges.Count;

        Assert.Throws<InvalidOperationException>(
            () => booking.MoveTo(BookingStatus.Confirmed, Actor, At));

        Assert.Equal(before, booking.StatusChanges.Count);
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
        var booking = Held();
        booking.Status = from;

        var wrong = Assert.Throws<InvalidOperationException>(() => booking.MoveTo(to, Actor, At));

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

    private static readonly Guid Actor = Guid.CreateVersion7();

    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;

    private static Booking Held() =>
        Booking.Hold(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            [new SlotPrice(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddDays(1), 200m)],
            DateTimeOffset.UtcNow);
}
