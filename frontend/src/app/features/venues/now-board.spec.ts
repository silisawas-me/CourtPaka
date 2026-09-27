import { VenueBooking } from '../../core/venues/venue-bookings.service';
import { arriving, courtsNow } from './now-board';

function booking(
  bookingId: string,
  hours: number[],
  overrides: Partial<VenueBooking> = {},
): VenueBooking {
  return {
    bookingId,
    bookerEmail: `${bookingId}@example.com`,
    bookerPhone: null,
    channel: 'Online',
    kind: 'App',
    customerName: null,
    customerPhone: null,
    status: 'Confirmed',
    arrival: 'Unconfirmed',
    arrivedAt: null,
    graceEndsAt: '2026-09-28T11:15:00Z',
    paymentState: 'Received',
    totalBaht: 200 * hours.length,
    takenBaht: 200 * hours.length,
    toPayBaht: 0,
    refundDueBaht: 0,
    sentBackBaht: 0,
    outstandingBaht: 0,
    slots: hours.map((hour) => ({
      courtId: 'c1',
      courtName: 'Court 1',
      date: '2026-09-28',
      hour,
      bahtPerHour: 200,
    })),
    can: {
      cancel: false,
      confirmArrival: false,
      checkIn: false,
      noShow: false,
      settlePayment: false,
      playedAfterAll: false,
      takeMoney: false,
      payWithPackage: false,
      extend: false,
      moveCourt: false,
      cancelChoices: [],
    },
    ...overrides,
  };
}

const HOURS = [17, 18, 19, 20, 21];

const day = {
  venue: { id: 'v1', name: 'Smash', addressLine: '', district: '', province: '' },
  date: '2026-09-28',
  lastBookableDate: '2026-10-28',
  opensHour: 17,
  closesHour: 22,
  courts: [
    {
      courtId: 'c1',
      name: 'Court 1',
      hours: HOURS.map((hour) => ({ hour, status: 'Free', bahtPerHour: 300 })),
    },
    {
      courtId: 'c2',
      name: 'Court 2',
      hours: HOURS.map((hour) => ({
        hour,
        status: hour === 18 ? 'Closed' : 'Free',
        bahtPerHour: 300,
      })),
    },
  ],
} as never;

describe('courtsNow, the floor this minute', () => {
  it('reads a run of hours as one game, and how much of it has gone', () => {
    // Two hours from six; it is half past six, so a quarter of the game has gone.
    const [court] = courtsNow(day, [booking('b1', [18, 19], { arrival: 'Arrived' })], {
      hour: 18,
      minute: 30,
    });

    expect(court.state).toBe('playing');
    if (court.state !== 'playing') return;
    expect([court.from, court.to]).toEqual([18, 20]);
    expect(court.done).toBeCloseTo(25, 5);
    expect(court.minutesLeft).toBe(90);
  });

  it('says a booked court is waiting on somebody until they come in', () => {
    const [court] = courtsNow(day, [booking('b1', [18])], { hour: 18, minute: 5 });

    expect(court.state).toBe('due');
  });

  it('gives a free court its price, and a closed one none', () => {
    const [free, closed] = courtsNow(day, [], { hour: 18, minute: 0 });

    expect(free).toMatchObject({ state: 'free', baht: 300 });
    expect(closed).toMatchObject({ state: 'closed', baht: null });
  });

  it('names who is next from the start of their run, not the rest of the game on now', () => {
    const [court] = courtsNow(
      day,
      [booking('now', [18, 19], { arrival: 'Arrived' }), booking('later', [20, 21])],
      { hour: 18, minute: 10 },
    );

    expect(court.next?.booking.bookingId).toBe('later');
    expect(court.next?.hour).toBe(20);
  });

  it('leaves out the hours a cancelled or no-show booking gave back', () => {
    const [court] = courtsNow(
      day,
      [
        booking('gone', [18], { status: 'Cancelled' }),
        booking('absent', [18], { status: 'NoShow' }),
      ],
      { hour: 18, minute: 0 },
    );

    expect(court.state).toBe('free');
  });
});

describe('arriving, the queue at the desk', () => {
  it('lists who starts within ninety minutes, soonest first, and nobody already in', () => {
    const queue = arriving(
      [
        booking('late', [20]),
        booking('soon', [19]),
        booking('here', [19], { arrival: 'Arrived' }),
        booking('far', [21]),
        booking('started', [18]),
      ],
      { hour: 18, minute: 40 },
    );

    expect(queue.map((one) => one.booking.bookingId)).toEqual(['soon', 'late']);
    expect(queue[0].inMinutes).toBe(20);
  });
});
