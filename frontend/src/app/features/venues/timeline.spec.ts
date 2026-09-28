import { Availability } from '../../core/venues/public-venue.service';
import { VenueBooking } from '../../core/venues/venue-bookings.service';
import { firstToShow, timelineRows, windowStart } from './timeline';

function booking(id: string, hours: number[], overrides: Partial<VenueBooking> = {}): VenueBooking {
  return {
    bookingId: id,
    bookerEmail: null,
    bookerPhone: null,
    channel: 'Staff',
    kind: 'WalkIn',
    customerName: `Name ${id}`,
    customerPhone: null,
    status: 'Confirmed',
    arrival: 'Unconfirmed',
    arrivedAt: null,
    graceEndsAt: '2026-09-30T12:15:00Z',
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
      date: '2026-09-30',
      hour,
      bahtPerHour: 200,
    })),
    can: { checkIn: false, cancelChoices: [] },
    ...overrides,
  } as unknown as VenueBooking;
}

const day = {
  courts: [
    {
      courtId: 'c1',
      name: 'Court 1',
      hours: [8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23].map((hour) => ({
        hour,
        status: hour < 10 ? 'Closed' : 'Free',
        bahtPerHour: 200,
      })),
    },
  ],
} as unknown as Availability;

describe('the timeline', () => {
  it('starts nine hours four before now, never past the ends of the day', () => {
    const hours = Array.from({ length: 16 }, (_, index) => 8 + index);
    expect(windowStart(hours, 18)).toBe(14);
    expect(windowStart(hours, 8)).toBe(8);
    // 23:00 is the last hour: the window cannot run past midnight.
    expect(windowStart(hours, 23)).toBe(15);
  });

  it('lays a booking out by its hours, and a shut stretch as its own block', () => {
    const [row] = timelineRows(
      day,
      [booking('b1', [14, 15], { can: { checkIn: true } as VenueBooking['can'] })],
      8,
      { hour: 12, minute: 0 },
      'Shut',
    );

    const shut = row.blocks.find((block) => block.kind === 'Shut')!;
    expect([shut.from, shut.to, shut.left]).toEqual([8, 10, 0]);

    const game = row.blocks.find((block) => block.booking)!;
    expect(game.name).toBe('Name b1');
    expect(game.left).toBeCloseTo((6 / 9) * 100);
    expect(game.width).toBeCloseTo((2 / 9) * 100);
    expect(game.due).toBe(true);
    expect(game.done).toBe(false);
  });

  it('leaves out what was called off, and fades what is over', () => {
    const [row] = timelineRows(
      day,
      [booking('gone', [11], { status: 'Cancelled' }), booking('over', [10])],
      10,
      { hour: 12, minute: 0 },
      'Shut',
    );

    expect(row.blocks.map((block) => block.booking?.bookingId)).toEqual(['over']);
    expect(row.blocks[0].done).toBe(true);
  });

  it('opens the panel on whoever the desk is waiting for first', () => {
    const playing = booking('playing', [18]);
    const waiting = booking('waiting', [19], { can: { checkIn: true } as VenueBooking['can'] });

    expect(firstToShow([playing, waiting], { hour: 18, minute: 30 })?.bookingId).toBe('waiting');
    expect(firstToShow([playing], { hour: 18, minute: 30 })?.bookingId).toBe('playing');
    expect(firstToShow([], { hour: 18, minute: 30 })).toBeNull();
  });
});
