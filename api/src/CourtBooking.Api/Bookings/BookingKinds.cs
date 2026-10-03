namespace CourtBooking.Api.Bookings;

/// <summary>
/// What kind of booking a row is, as the counter tells them apart on the floor: booked in the
/// app, sold at the counter, a group's standing week (US-30), or hours from a package (US-31).
/// </summary>
/// <remarks>
/// Read from what the booking already records — the channel, the series and the package it came
/// from — rather than stored, because a kind nobody wrote down is one that can never disagree
/// with the columns it is read from. A group's week paid for with a package is still the group's
/// week: the standing arrangement is what the counter is looking at when it sees the block.
/// </remarks>
public static class BookingKinds
{
    public const string App = "App";
    public const string WalkIn = "WalkIn";
    public const string Series = "Series";
    public const string Package = "Package";

    public static string Of(Booking booking) => booking switch
    {
        { SeriesId: not null } => Series,
        { PackageId: not null } => Package,
        { Channel: BookingChannel.Online } => App,
        _ => WalkIn,
    };
}
