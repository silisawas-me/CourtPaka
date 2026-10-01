import { VenueBooking } from '../../core/venues/venue-bookings.service';
import { span } from './timeline';

/** The list's filters, left to right as the design draws them. */
export const LIST_FILTERS = ['all', 'unpaid', 'waiting', 'cancelled', 'noShow'] as const;
export type ListFilter = (typeof LIST_FILTERS)[number];

/** How a row's status reads, and which of the status colours it wears (`--status-*`). */
export interface RowState {
  readonly key: string;
  readonly tone: 'playing' | 'confirmed' | 'waiting' | 'risk' | 'done';
}

/** Where a booking stands, in the words the list uses — from the server's status and doors. */
export function stateOf(booking: VenueBooking, nowMs: number): RowState {
  switch (booking.status) {
    case 'Cancelled':
      return { key: 'cancelled', tone: 'risk' };
    case 'NoShow':
      return { key: 'noShow', tone: 'risk' };
    case 'Rejected':
      return { key: 'rejected', tone: 'risk' };
    case 'Expired':
      return { key: 'expired', tone: 'done' };
    case 'Completed':
      return { key: 'done', tone: 'done' };
    case 'Held':
      return { key: 'held', tone: 'waiting' };
    case 'PendingVerification':
      return { key: 'slip', tone: 'waiting' };
  }
  if (booking.arrival === 'Arrived') {
    return { key: startedBy(booking, nowMs) ? 'playing' : 'checkedIn', tone: 'playing' };
  }
  return booking.can.checkIn
    ? { key: 'waiting', tone: 'waiting' }
    : { key: 'confirmed', tone: 'confirmed' };
}

/** Whether the booking's first hour has begun, on the Bangkok clock the slots are written in. */
function startedBy(booking: VenueBooking, nowMs: number): boolean {
  const at = (slot: { date: string; hour: number }) =>
    `${slot.date}T${String(slot.hour).padStart(2, '0')}`;
  const first = [...booking.slots].sort((a, b) => at(a).localeCompare(at(b)))[0];
  if (!first) {
    return false;
  }
  const startsAt = Date.parse(`${at(first)}:00:00+07:00`);
  return nowMs >= startsAt;
}

/** Whether a row belongs under a filter. Owing reads the server's door, never `toPayBaht > 0`. */
export function passes(booking: VenueBooking, filter: ListFilter): boolean {
  switch (filter) {
    case 'all':
      return true;
    case 'unpaid':
      return booking.can.takeMoney;
    case 'waiting':
      return booking.can.checkIn;
    case 'cancelled':
      return booking.status === 'Cancelled';
    case 'noShow':
      return booking.status === 'NoShow';
  }
}

/** How many rows each filter would leave. */
export function countsOf(day: readonly VenueBooking[]): Record<ListFilter, number> {
  return Object.fromEntries(
    LIST_FILTERS.map((filter) => [filter, day.filter((one) => passes(one, filter)).length]),
  ) as Record<ListFilter, number>;
}

/** Hours that were bought and still stand, what came in, and what is still owed. */
export function summaryOf(day: readonly VenueBooking[]): {
  hours: number;
  takenBaht: number;
  owingBaht: number;
} {
  const sold = day.filter(
    (one) => one.status !== 'Cancelled' && one.status !== 'Rejected' && one.status !== 'Expired',
  );
  return {
    hours: sold.reduce((sum, one) => sum + one.slots.length, 0),
    takenBaht: day.reduce((sum, one) => sum + one.takenBaht, 0),
    owingBaht: day.reduce((sum, one) => sum + (one.can.takeMoney ? one.toPayBaht : 0), 0),
  };
}

/** "18:00–20:00" — the hours a booking holds, as the list's first column writes them. */
export function timeOf(booking: VenueBooking): string {
  if (booking.slots.length === 0) {
    return '';
  }
  const { from, to } = span(booking);
  return `${String(from).padStart(2, '0')}:00–${String(to).padStart(2, '0')}:00`;
}

/** The courts a booking is on, in the order the venue names them. */
export function courtsOf(booking: VenueBooking): string {
  return [...new Set(booking.slots.map((slot) => slot.courtName))].join(', ');
}

/** The day a booking is played: its first hour's date. */
export function dayOf(booking: VenueBooking): string {
  return booking.slots.map((slot) => slot.date).sort()[0] ?? '';
}
