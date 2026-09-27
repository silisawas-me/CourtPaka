import { OpeningHours, WEEKDAYS } from '../../core/venues/court.service';
import { PriceBand } from '../../core/venues/pricing.service';
import { bandsOf, gridOf, openHours, paint } from './price-grid';

/** Open 06:00–22:00 every day but Sunday, which opens 08:00–20:00. */
const week: OpeningHours[] = [
  {
    id: 'w1',
    effectiveFrom: '2026-09-01',
    inForce: true,
    days: WEEKDAYS.map((day) =>
      day === 'Sunday'
        ? { day, opensHour: 8, closesHour: 20 }
        : { day, opensHour: 6, closesHour: 22 },
    ),
  },
];

/** The seed's prices: 200 until 18:00, 300 after, every day. */
const bands: PriceBand[] = WEEKDAYS.flatMap((day) => [
  { day, fromHour: 6, toHour: 18, bahtPerHour: 200 },
  { day, fromHour: 18, toHour: 22, bahtPerHour: 300 },
]);

describe('the painted week of prices', () => {
  const grid = gridOf(bands, openHours(week));

  it('spans the earliest opening to the latest close, and leaves shut hours unpainted', () => {
    expect(grid.hours[0]).toBe(6);
    expect(grid.hours[grid.hours.length - 1]).toBe(21);
    // Sunday opens at eight: its six and seven o'clock have no price and take no paint.
    expect(grid.cells.Sunday.slice(0, 2)).toEqual([null, null]);
  });

  it('reads the prices the venue charges as tiers, cheapest first, with a peak to spare', () => {
    expect(grid.tiers).toEqual([200, 300, 360]);
    expect(grid.cells.Monday[grid.hours.indexOf(17)]).toBe(0);
    expect(grid.cells.Monday[grid.hours.indexOf(18)]).toBe(1);
  });

  it('saves back exactly the bands it was read from', () => {
    // Sunday is open 8–20, so its bands are cut to the hours it is open.
    expect(bandsOf(grid)).toEqual(
      WEEKDAYS.flatMap((day): PriceBand[] =>
        day === 'Sunday'
          ? [
              { day, fromHour: 8, toHour: 18, bahtPerHour: 200 },
              { day, fromHour: 18, toHour: 20, bahtPerHour: 300 },
            ]
          : [
              { day, fromHour: 6, toHour: 18, bahtPerHour: 200 },
              { day, fromHour: 18, toHour: 22, bahtPerHour: 300 },
            ],
      ),
    );
  });

  it('turns a painted run into its own band, and never paints a shut hour', () => {
    let painted = paint(grid, 'Saturday', 19, 2);
    painted = paint(painted, 'Saturday', 20, 2);
    painted = paint(painted, 'Sunday', 6, 2);

    expect(bandsOf(painted).filter((band) => band.day === 'Saturday')).toEqual([
      { day: 'Saturday', fromHour: 6, toHour: 18, bahtPerHour: 200 },
      { day: 'Saturday', fromHour: 18, toHour: 19, bahtPerHour: 300 },
      { day: 'Saturday', fromHour: 19, toHour: 21, bahtPerHour: 360 },
      { day: 'Saturday', fromHour: 21, toHour: 22, bahtPerHour: 300 },
    ]);
    expect(painted.cells.Sunday[0]).toBeNull();
  });
});
