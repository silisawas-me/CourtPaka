import { VenueBooking, VenueBookingActions } from '../../core/venues/venue-bookings.service';
import { needsDoing } from './needs-doing';

function booking(
  bookingId: string,
  can: Partial<VenueBookingActions> = {},
  overrides: Partial<VenueBooking> = {},
): VenueBooking {
  return {
    bookingId,
    bookerEmail: 'player@example.com',
    bookerPhone: null,
    channel: 'Online',
    customerName: null,
    customerPhone: null,
    status: 'Confirmed',
    arrival: 'Unconfirmed',
    arrivedAt: null,
    graceEndsAt: '2026-09-21T11:15:00Z',
    paymentState: 'Received',
    totalBaht: 400,
    takenBaht: 400,
    toPayBaht: 0,
    refundDueBaht: 0,
    sentBackBaht: 0,
    outstandingBaht: 0,
    slots: [
      { courtId: 'c1', courtName: 'Court 1', date: '2026-09-21', hour: 18, bahtPerHour: 400 },
    ],
    can: {
      cancel: false,
      confirmArrival: false,
      checkIn: false,
      noShow: false,
      settlePayment: false,
      playedAfterAll: false,
      takeMoney: false,
      cancelChoices: [],
      ...can,
    },
    ...overrides,
  } as VenueBooking;
}

const who = (one: VenueBooking) => one.bookerEmail;
const when = () => '18:00–19:00';

describe('what the day is waiting for', () => {
  it('finds nothing where every door is shut', () => {
    expect(needsDoing([booking('b1')], who, when)).toEqual([]);
  });

  /**
   * The order is who is waiting: somebody standing at the desk before a decision about hours,
   * and hours before money, which waits better than a person does.
   */
  it('puts the person at the desk before the money', () => {
    const chores = needsDoing(
      [
        booking('money', { takeMoney: true }, { toPayBaht: 300 }),
        booking('gone', { noShow: true }),
        booking('here', { checkIn: true }),
      ],
      who,
      when,
    );

    expect(chores.map((chore) => chore.kind)).toEqual(['checkIn', 'noShow', 'takeMoney']);
  });

  it('carries the amount for the chores that are about money', () => {
    const chores = needsDoing(
      [
        booking('owing', { takeMoney: true }, { toPayBaht: 300 }),
        booking('owed', {}, { outstandingBaht: 150 }),
      ],
      who,
      when,
    );

    expect(chores).toEqual([
      {
        kind: 'takeMoney',
        bookingId: 'owing',
        who: 'player@example.com',
        when: '18:00–19:00',
        baht: 300,
      },
      {
        kind: 'refund',
        bookingId: 'owed',
        who: 'player@example.com',
        when: '18:00–19:00',
        baht: 150,
      },
    ]);
  });

  /**
   * The rule the venue's own rows follow: a booking that was let go has a difference between its
   * price and what was paid, and nobody owes it (PRD US-26).
   */
  it('does not ask for money the door says is not owed', () => {
    const chores = needsDoing([booking('b1', { takeMoney: false }, { toPayBaht: 300 })], who, when);

    expect(chores).toEqual([]);
  });

  it('lists one booking once for each thing it is waiting for', () => {
    const chores = needsDoing(
      [booking('b1', { checkIn: true, takeMoney: true }, { toPayBaht: 300 })],
      who,
      when,
    );

    expect(chores.map((chore) => chore.kind)).toEqual(['checkIn', 'takeMoney']);
  });
});
