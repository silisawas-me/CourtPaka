import { OpeningHours } from '../../core/venues/court.service';
import { PriceBand } from '../../core/venues/pricing.service';
import { dayIsValid, latestWeek, pricesCovering } from './hours-rules';

describe('the opening hours form', () => {
  it('starts from the latest week published, a day not in it shut', () => {
    const schedules = [
      {
        id: 'a',
        effectiveFrom: '2026-01-01',
        inForce: true,
        days: [{ day: 'Monday', opensHour: 6, closesHour: 22 }],
      },
      {
        id: 'b',
        effectiveFrom: '2026-10-01',
        inForce: false,
        days: [{ day: 'Monday', opensHour: 8, closesHour: 23 }],
      },
    ] as OpeningHours[];

    const week = latestWeek(schedules);
    expect(week).toHaveLength(7);
    expect(week.find((one) => one.day === 'Monday')).toEqual({
      day: 'Monday',
      opensHour: 8,
      closesHour: 23,
    });
    expect(week.find((one) => one.day === 'Sunday')).toEqual({
      day: 'Sunday',
      opensHour: null,
      closesHour: null,
    });
  });

  it('takes a day shut, or open before it closes', () => {
    expect(dayIsValid({ day: 'Monday', opensHour: null, closesHour: null })).toBe(true);
    expect(dayIsValid({ day: 'Monday', opensHour: 8, closesHour: 23 })).toBe(true);
    expect(dayIsValid({ day: 'Monday', opensHour: 23, closesHour: 8 })).toBe(false);
    expect(dayIsValid({ day: 'Monday', opensHour: 8, closesHour: null })).toBe(false);
  });

  it('prices an hour it opens from the nearest priced hour that day', () => {
    const bands: PriceBand[] = [
      { day: 'Monday', fromHour: 8, toHour: 17, bahtPerHour: 180 },
      { day: 'Monday', fromHour: 17, toHour: 23, bahtPerHour: 260 },
    ];

    const covered = pricesCovering(bands, [{ day: 'Monday', opensHour: 7, closesHour: 24 }])!;
    expect(covered.added).toBe(2);
    expect(covered.bands).toContainEqual({
      day: 'Monday',
      fromHour: 7,
      toHour: 8,
      bahtPerHour: 180,
    });
    expect(covered.bands).toContainEqual({
      day: 'Monday',
      fromHour: 23,
      toHour: 24,
      bahtPerHour: 260,
    });
    // Nothing new to price: the list goes back as it was.
    expect(pricesCovering(bands, [{ day: 'Monday', opensHour: 8, closesHour: 23 }])!.added).toBe(0);
  });

  it('prices a day with no prices at all from the cheapest hour, and a venue with none not at all', () => {
    const bands: PriceBand[] = [{ day: 'Monday', fromHour: 8, toHour: 23, bahtPerHour: 180 }];
    const covered = pricesCovering(bands, [{ day: 'Sunday', opensHour: 10, closesHour: 12 }])!;
    expect(
      covered.bands.filter((band) => band.day === 'Sunday').map((band) => band.bahtPerHour),
    ).toEqual([180, 180]);
    expect(pricesCovering([], [{ day: 'Sunday', opensHour: 10, closesHour: 12 }])).toBeNull();
  });
});
