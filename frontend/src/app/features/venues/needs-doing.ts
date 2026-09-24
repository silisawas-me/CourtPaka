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

  return spread(chores);
}

/**
 * A turn each, in the order above, until there is nothing left.
 *
 * Sorting by kind and then showing the first few would hide whole kinds on exactly the evening
 * they matter: six people to check in at seven o'clock, and the slip nobody has answered for and
 * the six hundred baht that never arrived are both below the fold, counted and not named. Every
 * kind that has anything says so, and the order decides who says it first.
 */
function spread(chores: readonly Chore[]): Chore[] {
  const queues = ORDER.map((kind) => chores.filter((chore) => chore.kind === kind));
  const spread: Chore[] = [];

  for (let turn = 0; spread.length < chores.length; turn++) {
    for (const queue of queues) {
      if (turn < queue.length) {
        spread.push(queue[turn]);
      }
    }
  }

  return spread;
}

/** Everything one booking is waiting for. A booking can be waiting for more than one thing. */
function kindsFor(booking: VenueBooking): ChoreKind[] {
  const kinds: ChoreKind[] = [];

  if (booking.can.checkIn) {
    kinds.push('checkIn');
  }

  // `can.noShow` is open on a booking that has already been played, because that is the door the
  // venue corrects a record through for a day afterwards (PRD 6.1, US-13). On its own row that
  // reads as "change what I wrote"; in a list headed "needs doing now" it would say nobody came —
  // about somebody who was checked in at the desk. Only hours being played, with nobody here.
  if (booking.can.noShow && booking.status === 'Confirmed' && booking.arrival !== 'Arrived') {
    kinds.push('noShow');
  }

  // The door, not the difference: a booking that was let go has a difference between its price
  // and what was paid, and nobody owes it (PRD US-26). The door already carries the amount being
  // above nothing, and a second copy of that rule here is a rule that can disagree.
  if (booking.can.takeMoney) {
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
