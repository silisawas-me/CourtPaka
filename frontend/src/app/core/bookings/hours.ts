import { clockHour } from '../i18n/clock.pipe';
import { BookingSlot } from './booking.service';

/**
 * The hours a booking holds, as the spans they are. Four hours in a row is one line; six o'clock
 * and eight o'clock with a gap between them is two, because writing it as 18:00 – 21:00 would
 * claim an hour nobody paid for.
 *
 * One set of hours, not one per slot: a booking can hold the same hour on several courts, which
 * is what a group of eight playing at six o'clock looks like.
 */
export function hoursOf(slots: BookingSlot[]): string {
  const taken = [...new Set(slots.map((slot) => slot.hour))].sort((first, next) => first - next);

  const spans: [number, number][] = [];
  for (const hour of taken) {
    const last = spans.at(-1);
    if (last && last[1] === hour) {
      last[1] = hour + 1;
    } else {
      spans.push([hour, hour + 1]);
    }
  }

  return spans.map(([from, to]) => `${clock(from)} – ${clock(to)}`).join(', ');
}

/** The courts a booking covers, each named once however many hours it holds. */
export function courtsOf(slots: BookingSlot[]): string {
  return [...new Set(slots.map((slot) => slot.courtName))].join(', ');
}

function clock(hour: number): string {
  return clockHour(hour, true);
}
