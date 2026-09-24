import { VenueBooking } from '../../core/venues/venue-bookings.service';

/**
 * A thing the counter has to do about one booking, in the order a shift meets them.
 *
 * Ordered by who is waiting: somebody standing at the desk first, then a decision that puts hours
 * back on sale, then money — which waits better than a person does.
 */
export type ChoreKind = 'checkIn' | 'noShow' | 'takeMoney' | 'settle' | 'refund';

const ORDER: readonly ChoreKind[] = ['checkIn', 'noShow', 'takeMoney', 'settle', 'refund'];

/** One line on the list: what to do, about whom, and the amount where there is one. */
export interface Chore {
  readonly kind: ChoreKind;
  readonly bookingId: string;
  /** Whoever the row names: a customer at the counter, or a booker's address (PRD US-13). */
  readonly who: string | null;
  /** The hours it is about, as the row already writes them. */
  readonly when: string;
  /** Baht, where the chore is about money. Null otherwise. */
  readonly baht: number | null;
}

/**
 * What is waiting to be done on this day, from what the day's rows already say (PRD US-25).
 *
 * Every one of these is a door the server has already decided is open — `can` comes back with the
 * day and nothing here second-guesses it (PRD US-13). What this adds is only the gathering: a
 * counter reading twenty rows to find the two that need something is a counter reading twenty
 * rows, and the two are why they looked.
 */
export function needsDoing(
  bookings: readonly VenueBooking[],
  who: (booking: VenueBooking) => string | null,
  when: (booking: VenueBooking) => string,
): Chore[] {
  const chores = bookings.flatMap((booking) =>
    kindsFor(booking).map((kind) => ({
      kind,
      bookingId: booking.bookingId,
      who: who(booking),
      when: when(booking),
      baht: bahtFor(kind, booking),
    })),
  );

  return chores.sort((one, other) => ORDER.indexOf(one.kind) - ORDER.indexOf(other.kind));
}

/** Everything one booking is waiting for. A booking can be waiting for more than one thing. */
function kindsFor(booking: VenueBooking): ChoreKind[] {
  const kinds: ChoreKind[] = [];

  if (booking.can.checkIn) {
    kinds.push('checkIn');
  }

  if (booking.can.noShow) {
    kinds.push('noShow');
  }

  // The door, not the difference: a booking that was let go has a difference between its price
  // and what was paid, and nobody owes it (PRD US-26).
  if (booking.can.takeMoney && booking.toPayBaht > 0) {
    kinds.push('takeMoney');
  }

  if (booking.can.settlePayment) {
    kinds.push('settle');
  }

  if (booking.outstandingBaht > 0) {
    kinds.push('refund');
  }

  return kinds;
}

function bahtFor(kind: ChoreKind, booking: VenueBooking): number | null {
  switch (kind) {
    case 'takeMoney':
      return booking.toPayBaht;
    case 'refund':
      return booking.outstandingBaht;
    default:
      return null;
  }
}
