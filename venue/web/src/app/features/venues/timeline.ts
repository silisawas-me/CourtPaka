import { Availability } from '../../core/venues/public-venue.service';
import { BookingKind, VenueBooking } from '../../core/venues/venue-bookings.service';
import { WallClock, whoIs } from './now-board';

/** As many hours as the design draws across the timeline. */
export const TIMELINE_HOURS = 9;

/** One stretch on a court's track: a booking's run of hours, or hours the court is shut. */
export interface TimelineBlock {
  /** Stable across reads, so a selected block stays selected when the day is read again. */
  key: string;
  booking: VenueBooking | null;
  /** App / WalkIn / Series / Package, or 'Shut' for a closure. */
  kind: BookingKind | 'Shut';
  name: string;
  from: number;
  to: number;
  /** Played out already: drawn faded, as the design does. */
  done: boolean;
  /** Waiting to be taken in — the check-in door is open (the rust dot). */
  due: boolean;
  /** Where on the track, as percentages of the window. */
  left: number;
  width: number;
}

/** An hour of a court that can still be sold today: a place on the track to tap and book. */
export interface OpenCell {
  hour: number;
  baht: number;
  left: number;
  width: number;
}

export interface TimelineRow {
  courtId: string;
  name: string;
  blocks: TimelineBlock[];
  /** Hours free to sell from the hour the clock is in, drawn as empty cells to tap. */
  open: OpenCell[];
}

/**
 * Where the nine hours start: four before now, as the design sits 18:45 in a 14:00–23:00 window
 * — what just happened on the left, the evening ahead on the right — never past the day's ends.
 */
export function windowStart(hours: readonly number[], nowHour: number): number {
  if (hours.length === 0) {
    return 0;
  }
  const first = hours[0];
  const last = hours[hours.length - 1] + 1;
  return Math.max(first, Math.min(nowHour - 4, last - TIMELINE_HOURS));
}

/** Every hour the venue sells today, earliest to latest, from the day's grid. */
export function dayHours(day: Availability): number[] {
  const all = day.courts.flatMap((court) => court.hours.map((hour) => hour.hour));
  if (all.length === 0) {
    return [];
  }
  const first = Math.min(...all);
  return Array.from({ length: Math.max(...all) - first + 1 }, (_, index) => first + index);
}

function standing(booking: VenueBooking): boolean {
  return (
    booking.status !== 'Cancelled' &&
    booking.status !== 'Expired' &&
    booking.status !== 'NoShow' &&
    booking.status !== 'Rejected'
  );
}

/** Consecutive hours, as [from, to) runs. */
function runsOf(hours: number[]): [number, number][] {
  const sorted = [...new Set(hours)].sort((left, right) => left - right);
  const runs: [number, number][] = [];
  for (const hour of sorted) {
    const last = runs[runs.length - 1];
    if (last && last[1] === hour) {
      last[1] = hour + 1;
    } else {
      runs.push([hour, hour + 1]);
    }
  }
  return runs;
}

/**
 * Each court as a track across the window: a block per run of one booking's hours, and a block
 * for hours the court is shut. Clipped to the window; what falls outside it is not drawn.
 */
export function timelineRows(
  day: Availability,
  bookings: readonly VenueBooking[],
  start: number,
  now: WallClock,
  shutLabel: string,
): TimelineRow[] {
  const end = start + TIMELINE_HOURS;
  const nowHours = now.hour + now.minute / 60;
  const live = bookings.filter(standing);

  const place = (from: number, to: number) => {
    const shownFrom = Math.max(from, start);
    const shownTo = Math.min(to, end);
    return shownTo <= shownFrom
      ? null
      : {
          left: ((shownFrom - start) / TIMELINE_HOURS) * 100,
          width: ((shownTo - shownFrom) / TIMELINE_HOURS) * 100,
        };
  };

  return day.courts.map((court) => {
    const blocks: TimelineBlock[] = [];

    for (const booking of live) {
      const hours = booking.slots
        .filter((slot) => slot.courtId === court.courtId)
        .map((slot) => slot.hour);
      for (const [from, to] of runsOf(hours)) {
        const at = place(from, to);
        if (at) {
          blocks.push({
            key: `${booking.bookingId}:${court.courtId}:${from}`,
            booking,
            kind: booking.kind,
            name: whoIs(booking) ?? '',
            from,
            to,
            done: to <= nowHours,
            due: booking.can.checkIn,
            ...at,
          });
        }
      }
    }

    const shut = court.hours.filter((hour) => hour.status === 'Closed').map((hour) => hour.hour);
    for (const [from, to] of runsOf(shut)) {
      const at = place(from, to);
      if (at) {
        blocks.push({
          key: `shut:${court.courtId}:${from}`,
          booking: null,
          kind: 'Shut',
          name: shutLabel,
          from,
          to,
          done: to <= nowHours,
          due: false,
          ...at,
        });
      }
    }

    // What the counter could still sell here: on sale with a price, not taken by anybody the
    // day's list knows of (it is read more often than the grid), and not already over — the
    // hour the clock is in still sells, as the counter's own door allows (PRD US-13).
    const taken = new Set(
      live.flatMap((booking) =>
        booking.slots.filter((slot) => slot.courtId === court.courtId).map((slot) => slot.hour),
      ),
    );
    const open: OpenCell[] = court.hours
      .filter(
        (hour) =>
          hour.status === 'Free' &&
          hour.bahtPerHour != null &&
          hour.hour >= now.hour &&
          hour.hour >= start &&
          hour.hour < end &&
          !taken.has(hour.hour),
      )
      .map((hour) => ({
        hour: hour.hour,
        baht: hour.bahtPerHour!,
        left: ((hour.hour - start) / TIMELINE_HOURS) * 100,
        width: 100 / TIMELINE_HOURS,
      }));

    return {
      courtId: court.courtId,
      name: court.name,
      blocks: blocks.sort((left, right) => left.from - right.from),
      open,
    };
  });
}

/**
 * The booking the panel opens on: whoever the desk is waiting for, soonest first; otherwise the
 * game on court now; otherwise the next one to start.
 */
export function firstToShow(
  bookings: readonly VenueBooking[],
  now: WallClock,
): VenueBooking | null {
  const live = bookings.filter(standing).filter((booking) => booking.slots.length > 0);
  const startOf = (booking: VenueBooking) => Math.min(...booking.slots.map((slot) => slot.hour));
  const endOf = (booking: VenueBooking) => Math.max(...booking.slots.map((slot) => slot.hour)) + 1;
  const bySoonest = [...live].sort((left, right) => startOf(left) - startOf(right));

  return (
    bySoonest.find((booking) => booking.can.checkIn) ??
    bySoonest.find((booking) => startOf(booking) <= now.hour && endOf(booking) > now.hour) ??
    bySoonest.find((booking) => startOf(booking) > now.hour) ??
    null
  );
}

/** "19:00–21:00": the run a booking plays, as the panel and the block say it. */
export function span(booking: VenueBooking): { from: number; to: number; hours: number } {
  const hours = booking.slots.map((slot) => slot.hour);
  const from = Math.min(...hours);
  const to = Math.max(...hours) + 1;
  return { from, to, hours: new Set(hours).size };
}
