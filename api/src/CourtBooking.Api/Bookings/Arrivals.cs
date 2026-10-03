namespace CourtBooking.Api.Bookings;

/// <summary>
/// When the counter may write down that somebody is coming, and when it may take them in
/// (PRD US-24). One place, so the buttons the page draws and the endpoints behind them cannot
/// disagree — the same arrangement the rest of the counter's doors use (VenueDecisions).
/// </summary>
public static class Arrivals
{
    /// <summary>
    /// Saying "they called, they are coming" only means something before they are here, and only
    /// about a booking that is still going to happen.
    /// </summary>
    public static bool CanConfirm(Booking booking, BookingStatus status) =>
        booking.Arrival < BookingArrival.Confirmed
        && status is BookingStatus.PendingVerification or BookingStatus.Confirmed;

    /// <summary>
    /// Taking somebody in: their booking is paid for, their hours have not finished, and they are
    /// not so early that it is somebody else's court they are standing on.
    /// </summary>
    public static bool CanCheckIn(Booking booking, BookingStatus status, DateTimeOffset now)
    {
        if (booking.Arrival == BookingArrival.Arrived || booking.Slots.Count == 0)
        {
            return false;
        }

        // Asked of what is stored rather than what the clock makes of it: a booking being played
        // reads as Confirmed until its last hour ends, which is exactly when somebody walks in.
        if (status is not (BookingStatus.Confirmed or BookingStatus.Completed))
        {
            return false;
        }

        var startsAt = booking.Slots.Min(slot => slot.StartsAt);
        var endsAt = booking.Slots.Max(slot => slot.EndsAt);
        return now >= startsAt - ArrivalTransitions.CheckInOpensBefore && now < endsAt;
    }
}
