import { Availability } from '../../core/venues/public-venue.service';

/** The lengths a walk-in can be sold for, in whole hours (O2: the hour is the unit here). */
export const WALK_IN_HOURS = [1, 2, 3] as const;

/** How many start times the modal offers: now, and the next few hours, as the design does. */
export const WALK_IN_STARTS = 4;

/**
 * The hours a walk-in can start at: the hour the clock is in (somebody standing at the counter
 * can have the rest of it, PRD US-13) and the hours after it, while any court is free at that
 * hour. Null `nowHour` means the day is not today, and then the day's first hours are offered.
 */
export function startOptions(day: Availability, nowHour: number | null): number[] {
  const hours = [...new Set(day.courts.flatMap((court) => court.hours.map((h) => h.hour)))].sort(
    (a, b) => a - b,
  );

  return hours
    .filter((hour) => nowHour === null || hour >= nowHour)
    .filter((hour) => day.courts.some((court) => freeAt(court, hour)))
    .slice(0, WALK_IN_STARTS);
}

/** The courts free for every hour of the run, in the grid's order. */
export function freeCourts(day: Availability, start: number, hours: number): string[] {
  return day.courts
    .filter((court) =>
      Array.from({ length: hours }, (_, index) => start + index).every((hour) =>
        freeAt(court, hour),
      ),
    )
    .map((court) => court.courtId);
}

/**
 * What the run costs on a court, from the prices the grid carries — or, before a court is chosen,
 * on the first court free for it, which is what the design's button shows. Null when nothing is.
 */
export function priceOf(
  day: Availability,
  start: number,
  hours: number,
  courtId: string | null,
): number | null {
  const court = day.courts.find(
    (one) => one.courtId === (courtId ?? freeCourts(day, start, hours)[0]),
  );
  if (!court) {
    return null;
  }

  let total = 0;
  for (let hour = start; hour < start + hours; hour++) {
    const price = court.hours.find((one) => one.hour === hour)?.bahtPerHour;
    if (price == null) {
      return null;
    }
    total += price;
  }
  return total;
}

function freeAt(court: Availability['courts'][number], hour: number): boolean {
  return court.hours.some((one) => one.hour === hour && one.status === 'Free');
}
