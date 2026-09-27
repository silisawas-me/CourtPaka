import { Availability } from '../../core/venues/public-venue.service';
import { freeCourts, priceOf, startOptions } from './walk-in-rules';

/** Two courts, 17:00 to 22:00; court 1 taken at 19:00, court 2 taken from 18:00 to 20:00. */
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
      hours: [17, 18, 19, 20, 21].map((hour) => ({
        hour,
        status: hour === 19 ? 'Booked' : 'Free',
        bahtPerHour: hour >= 18 ? 300 : 200,
      })),
    },
    {
      courtId: 'c2',
      name: 'Court 2',
      hours: [17, 18, 19, 20, 21].map((hour) => ({
        hour,
        status: hour === 18 || hour === 19 ? 'Booked' : 'Free',
        bahtPerHour: hour >= 18 ? 300 : 200,
      })),
    },
  ],
} as Availability;

describe('the walk-in rules', () => {
  it('starts now or later, and only where some court is free', () => {
    expect(startOptions(day, 18)).toEqual([18, 20, 21]);
    expect(startOptions(day, null)).toEqual([17, 18, 20, 21]);
  });

  it('offers only the courts free for the whole run', () => {
    expect(freeCourts(day, 17, 1)).toEqual(['c1', 'c2']);
    expect(freeCourts(day, 17, 2)).toEqual(['c1']);
    expect(freeCourts(day, 18, 2)).toEqual([]);
  });

  it('prices the run from the grid, on the court chosen or the first one free', () => {
    expect(priceOf(day, 17, 2, 'c1')).toBe(500);
    expect(priceOf(day, 20, 2, null)).toBe(600);
    expect(priceOf(day, 18, 2, null)).toBeNull();
  });
});
