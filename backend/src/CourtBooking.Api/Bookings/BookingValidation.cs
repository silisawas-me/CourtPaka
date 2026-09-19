using CourtBooking.Api.Localization;
using CourtBooking.Api.Venues;

namespace CourtBooking.Api.Bookings;

/// <summary>
/// What a booking request has to satisfy before anything is written. The rules that need the
/// venue's settings are in <see cref="BookingEndpoints"/>, where the settings are loaded; these are
/// the ones that only need the request and the clock.
/// </summary>
public static class BookingValidation
{
    /// <summary>
    /// An hour has to start at least this far ahead (PRD S-25). Someone who turns up to play needs
    /// time to get there, and the venue needs to see the booking before the hour starts.
    /// </summary>
    public static readonly TimeSpan LeadTime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A day at a large venue is around a dozen courts by sixteen hours. This is well past any real
    /// booking while still bounding what one request can ask the database to check.
    /// </summary>
    public const int MaxSlots = 24;

    public static string? Validate(
        IReadOnlyCollection<BookingSlotRequest> slots,
        DateTimeOffset now,
        DateOnly today)
    {
        if (slots.Count == 0 || slots.Any(slot => slot is null))
        {
            return BookingErrorCodes.NoSlots;
        }

        if (slots.Count > MaxSlots)
        {
            return BookingErrorCodes.TooManySlots;
        }

        if (slots.Distinct().Count() != slots.Count)
        {
            return BookingErrorCodes.DuplicateSlot;
        }

        // A booking is made from one grid, and one grid is one day. It also bounds the work: the
        // settings in force have to be read once per day the booking touches.
        if (slots.Select(slot => slot.Date).Distinct().Count() > 1)
        {
            return BookingErrorCodes.MoreThanOneDay;
        }

        foreach (var slot in slots)
        {
            if (slot.Hour < CourtValidation.EarliestOpeningHour
                || slot.Hour > CourtValidation.LatestOpeningHour)
            {
                return BookingErrorCodes.HourNotAvailable;
            }

            // The same window the grid is drawn for, so an hour that cannot be looked at cannot be
            // booked either (PRD S-04).
            if (Availability.ValidateDate(slot.Date, today) is { } outsideWindow)
            {
                return outsideWindow;
            }

            if (PlatformRequirements.BangkokHour(slot.Date, slot.Hour) - now < LeadTime)
            {
                return BookingErrorCodes.StartsTooSoon;
            }
        }

        return null;
    }
}
