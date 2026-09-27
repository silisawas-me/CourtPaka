import { Availability } from '../../core/venues/public-venue.service';
import { VenueBooking } from '../../core/venues/venue-bookings.service';

/** A minute of the venue's day, on a Bangkok wall: what `venueNow()` hands back. */
export interface WallClock {
  hour: number;
  minute: number;
}

/** A court at this minute (badPaka 2b). */
export type CourtNow =
  | {
      courtId: string;
      name: string;
      state: 'playing' | 'due';
      booking: VenueBooking;
      /** The run of hours on this court the current one belongs to, as [from, to). */
      from: number;
      to: number;
      /** How much of that run has gone, 0–100, and the minutes left of it. */
      done: number;
      minutesLeft: number;
      next: NextOnCourt | null;
    }
  | {
      courtId: string;
      name: string;
      state: 'free' | 'closed' | 'shut';
      /** What this hour costs, for a free court with a price; null otherwise. */
      baht: number | null;
      next: NextOnCourt | null;
    };

/** Who is on this court next, and at what hour. */
export interface NextOnCourt {
  hour: number;
  booking: VenueBooking;
}

/** Somebody whose first hour starts within the window, and on which courts. */
export interface Arriving {
  booking: VenueBooking;
  hour: number;
  /** Minutes from now until their first hour starts. */
  inMinutes: number;
  courts: string;
}

/** How far ahead the queue at the desk looks: the next hour and a half. */
export const ARRIVING_WITHIN_MINUTES = 90;

/**
 * Bookings that stand on the floor. A cancelled or expired one has given its hours back, and a
 * no-show has had them released for walk-ins (PRD US-13), so none of them is on a court now.
 */
function standing(booking: VenueBooking): boolean {
  return (
    booking.status !== 'Cancelled' && booking.status !== 'Expired' && booking.status !== 'NoShow'
  );
}

/**
 * Every court as it is this minute, from the two answers the day console already reads — the
 * venue's grid of hours and the day's bookings — so the counter's two screens can never tell two
 * different stories about the same court.
 */
export function courtsNow(
  day: Availability,
  bookings: readonly VenueBooking[],
  now: WallClock,
): CourtNow[] {
  const live = bookings.filter(standing);

  return day.courts.map((court) => {
    const onCourt = live.flatMap((booking) =>
      booking.slots
        .filter((slot) => slot.courtId === court.courtId)
        .map((slot) => ({ booking, hour: slot.hour })),
    );

    const next =
      onCourt
        .filter((one) => one.hour > now.hour)
        // The start of somebody's run, not an hour in the middle of the one on court now.
        .filter(
          (one) =>
            !onCourt.some((other) => other.booking === one.booking && other.hour === one.hour - 1),
        )
        .sort((left, right) => left.hour - right.hour)[0] ?? null;

    const current = onCourt.find((one) => one.hour === now.hour);
    if (current) {
      const hours = new Set(
        onCourt.filter((one) => one.booking === current.booking).map((one) => one.hour),
      );
      let from = now.hour;
      while (hours.has(from - 1)) from--;
      let to = now.hour + 1;
      while (hours.has(to)) to++;

      const length = (to - from) * 60;
      const gone = (now.hour - from) * 60 + now.minute;
      return {
        courtId: court.courtId,
        name: court.name,
        state: current.booking.arrival === 'Arrived' ? 'playing' : 'due',
        booking: current.booking,
        from,
        to,
        done: Math.min(100, Math.max(0, (gone / length) * 100)),
        minutesLeft: Math.max(0, length - gone),
        next,
      };
    }

    const hour = court.hours.find((one) => one.hour === now.hour);
    const state = !hour ? 'shut' : hour.status === 'Closed' ? 'closed' : 'free';

    return {
      courtId: court.courtId,
      name: court.name,
      state,
      baht: state === 'free' ? (hour?.bahtPerHour ?? null) : null,
      next,
    };
  });
}

/**
 * Who is due at the desk in the next ninety minutes, soonest first: the first hour of each
 * standing booking that has not come in yet. Somebody already here is not arriving.
 */
export function arriving(bookings: readonly VenueBooking[], now: WallClock): Arriving[] {
  const minuteOfDay = now.hour * 60 + now.minute;

  return bookings
    .filter(standing)
    .filter((booking) => booking.arrival !== 'Arrived' && booking.slots.length > 0)
    .map((booking) => {
      const hour = Math.min(...booking.slots.map((slot) => slot.hour));
      const courts = [
        ...new Set(booking.slots.filter((slot) => slot.hour === hour).map((s) => s.courtName)),
      ].join(', ');
      return { booking, hour, inMinutes: hour * 60 - minuteOfDay, courts };
    })
    .filter((one) => one.inMinutes > 0 && one.inMinutes <= ARRIVING_WITHIN_MINUTES)
    .sort((left, right) => left.inMinutes - right.inMinutes);
}

/** The name a counter calls out: the one they gave, their address before the @, or a phone. */
export function whoIs(booking: VenueBooking): string | null {
  return booking.customerName ?? booking.bookerEmail?.split('@')[0] ?? booking.bookerPhone ?? null;
}
