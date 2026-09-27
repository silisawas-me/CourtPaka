import { OpeningHours, Weekday, WEEKDAYS } from '../../core/venues/court.service';
import { PriceBand } from '../../core/venues/pricing.service';

/**
 * The week of prices as the design paints it (owner app PR-4): a row per day, a cell per hour,
 * each open cell painted with one of a few price tiers. It is the same data the server keeps —
 * `PriceBand`, a day and a run of hours at one price (O11) — laid out so a venue sees its peak.
 *
 * Tier numbers index into the tier prices; `null` is an hour the venue is not open, which has no
 * price and is not painted.
 */
export interface PriceGrid {
  /** The hours across the top: the earliest opening to the latest close of any day. */
  readonly hours: readonly number[];
  /** The price of each tier, cheapest first. */
  readonly tiers: readonly number[];
  /** Per day, per hour of `hours`: the tier painted there, or null where the venue is shut. */
  readonly cells: Readonly<Record<Weekday, readonly (number | null)[]>>;
}

/**
 * The hours each day is open, in any week the venue has published — the current one and any
 * dated ahead — because a price has to cover every hour the venue will be open (the server
 * refuses a week that leaves one without a price).
 */
export function openHours(schedules: readonly OpeningHours[]): Record<Weekday, Set<number>> {
  const open = Object.fromEntries(WEEKDAYS.map((day) => [day, new Set<number>()])) as Record<
    Weekday,
    Set<number>
  >;

  for (const week of schedules) {
    for (const day of week.days) {
      if (day.opensHour === null || day.closesHour === null) {
        continue;
      }
      for (let hour = day.opensHour; hour < day.closesHour; hour++) {
        open[day.day].add(hour);
      }
    }
  }

  return open;
}

/**
 * The painted week a venue's bands make. The tiers are the prices the bands use, cheapest first
 * — a venue that charges 200 and 300 has two tiers — padded to at least `least` by adding a step
 * above the dearest, so there is always a peak to paint with.
 */
export function gridOf(
  bands: readonly PriceBand[],
  open: Record<Weekday, Set<number>>,
  least = 3,
  step = 60,
): PriceGrid {
  const allOpen = WEEKDAYS.flatMap((day) => [...open[day]]);
  const hours =
    allOpen.length === 0
      ? []
      : Array.from(
          { length: Math.max(...allOpen) - Math.min(...allOpen) + 1 },
          (_, index) => Math.min(...allOpen) + index,
        );

  const tiers = [...new Set(bands.map((band) => band.bahtPerHour))].sort((a, b) => a - b);
  if (tiers.length === 0) {
    tiers.push(200);
  }
  while (tiers.length < least) {
    tiers.push(tiers[tiers.length - 1] + step);
  }

  const priceAt = (day: Weekday, hour: number) =>
    bands.find((band) => band.day === day && band.fromHour <= hour && hour < band.toHour)
      ?.bahtPerHour;

  const cells = Object.fromEntries(
    WEEKDAYS.map((day) => [
      day,
      hours.map((hour) => {
        if (!open[day].has(hour)) {
          return null;
        }
        const price = priceAt(day, hour);
        // An open hour the bands missed reads as the cheapest tier, to be painted over.
        return price === undefined ? 0 : tiers.indexOf(price);
      }),
    ]),
  ) as Record<Weekday, (number | null)[]>;

  return { hours, tiers, cells };
}

/** The same week painted one cell differently. Shut hours stay unpainted. */
export function paint(grid: PriceGrid, day: Weekday, hour: number, tier: number): PriceGrid {
  const index = grid.hours.indexOf(hour);
  if (index === -1 || grid.cells[day][index] === null || grid.cells[day][index] === tier) {
    return grid;
  }

  return {
    ...grid,
    cells: {
      ...grid.cells,
      [day]: grid.cells[day].map((cell, at) => (at === index ? tier : cell)),
    },
  };
}

/** The bands a painted week saves as: each run of one tier on one day is one band. */
export function bandsOf(grid: PriceGrid): PriceBand[] {
  const bands: PriceBand[] = [];

  for (const day of WEEKDAYS) {
    let from: number | null = null;
    let tier: number | null = null;

    grid.hours.forEach((hour, index) => {
      const here = grid.cells[day][index];
      if (here !== tier) {
        if (from !== null && tier !== null) {
          bands.push({ day, fromHour: from, toHour: hour, bahtPerHour: grid.tiers[tier] });
        }
        from = here === null ? null : hour;
        tier = here;
      }
    });

    if (from !== null && tier !== null) {
      const end = grid.hours[grid.hours.length - 1] + 1;
      bands.push({ day, fromHour: from, toHour: end, bahtPerHour: grid.tiers[tier] });
    }
  }

  return bands;
}
