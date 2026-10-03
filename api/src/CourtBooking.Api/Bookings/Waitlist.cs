using CourtBooking.Api.Identity;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Bookings;

/// <summary>Where somebody stands in the queue for a day that is full (PRD US-27).</summary>
public enum WaitlistState
{
    /// <summary>Waiting for an hour to come free.</summary>
    Waiting = 1,

    /// <summary>Hours have been put aside for them and they have not answered yet.</summary>
    Offered = 2,

    /// <summary>They took the offer, and it is a booking like any other.</summary>
    Taken = 3,

    /// <summary>They stood down, or the day went past with nothing to give them.</summary>
    Gone = 4,
}

/// <summary>
/// Somebody who wanted hours a venue did not have (PRD US-27). A queue exists because a full day
/// is not the end of the conversation: hours come back — a booking is cancelled, nobody turns up,
/// a hold runs out — and the venue would rather sell them again than let them go quiet.
///
/// What is asked for is a window and a length, not an hour: "two hours some time between six and
/// ten" is what somebody actually wants, and it is what lets an hour that comes free at seven be
/// offered to them at all.
/// </summary>
public sealed class WaitlistEntry
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid VenueId { get; init; }

    public required Guid BookerUserId { get; init; }

    /// <summary>The venue's own day, not the asker's (PRD BR-10).</summary>
    public required DateOnly Date { get; init; }

    /// <summary>The earliest they would start, as an hour of that day.</summary>
    public required int FromHour { get; init; }

    /// <summary>The latest they would still be playing, so 22 means "finished by ten".</summary>
    public required int UntilHour { get; init; }

    /// <summary>How many hours in a row they want inside that window.</summary>
    public required int Hours { get; init; }

    public required DateTimeOffset AskedAt { get; init; }

    public WaitlistState State { get; set; } = WaitlistState.Waiting;

    /// <summary>
    /// The hold put aside for them, once there is one. An offer is an ordinary booking — the
    /// same fifteen minutes, the same way of paying — and this is the only thread between it and
    /// the queue it came from (PRD US-27).
    /// </summary>
    public Guid? OfferedBookingId { get; set; }

    /// <summary>When they stopped waiting, whichever way it ended.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    public Booking? OfferedBooking { get; init; }

    public Venue? Venue { get; init; }

    public AppUser? Booker { get; init; }
}

/// <summary>
/// What a queue will accept, in one place, so the screen that asks and the endpoint that writes
/// agree about it (PRD US-27).
/// </summary>
public static class Waitlist
{
    /// <summary>
    /// The longest run anybody may wait for. Long enough for a doubles booking that wants the
    /// evening; short enough that one entry cannot hold the shape of a whole day's offers.
    /// </summary>
    public const int MaxHours = 4;

    /// <summary>
    /// Why a queue would not take this, or null. The window has to be a window, the run has to
    /// fit inside it, and the day has to be one the venue is selling (PRD US-27, S-04).
    /// </summary>
    public static string? Refusal(DateOnly date, int fromHour, int untilHour, int hours, DateOnly today)
    {
        if (date < today || date > today.AddDays(Availability.BookableDaysAhead))
        {
            return WaitlistErrorCodes.DayNotOpen;
        }

        if (fromHour is < 0 or > 23 || untilHour is < 1 or > 24 || untilHour <= fromHour)
        {
            return WaitlistErrorCodes.WindowNotAWindow;
        }

        return hours < 1 || hours > MaxHours || hours > untilHour - fromHour
            ? WaitlistErrorCodes.HoursDoNotFit
            : null;
    }
}

public static class WaitlistErrorCodes
{
    /// <summary>Before today, or further ahead than the venue sells.</summary>
    public const string DayNotOpen = "waitlist.day_not_open";

    public const string WindowNotAWindow = "waitlist.window_not_a_window";

    /// <summary>More hours than the window holds, none at all, or more than anyone may ask.</summary>
    public const string HoursDoNotFit = "waitlist.hours_do_not_fit";

    /// <summary>They are already in this venue's queue for that day.</summary>
    public const string AlreadyWaiting = "waitlist.already_waiting";

    public const string NotFound = "waitlist.not_found";
}
