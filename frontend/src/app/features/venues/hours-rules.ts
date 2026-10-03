import { OpeningHours, OpeningHoursDay, WEEKDAYS } from '../../core/venues/court.service';
import { PriceBand } from '../../core/venues/pricing.service';

/**
 * The week a venue's opening-hours form starts from: the latest one it has published (a week
 * dated ahead is the one it is about to run on), or shut every day when it has none yet.
 */
export function latestWeek(schedules: readonly OpeningHours[]): OpeningHoursDay[] {
  const latest = [...schedules].sort((one, other) =>
    one.effectiveFrom.localeCompare(other.effectiveFrom),
  )[schedules.length - 1];
  return WEEKDAYS.map(
    (day) =>
      latest?.days.find((one) => one.day === day) ?? { day, opensHour: null, closesHour: null },
  );
}

/** Whether a day's two hours make an opening: shut all day, or open before it closes. */
export function dayIsValid(day: OpeningHoursDay): boolean {
  return (
    (day.opensHour === null && day.closesHour === null) ||
    (day.opensHour !== null && day.closesHour !== null && day.opensHour < day.closesHour)
  );
}

/** The latest a venue may close: 06:00 the next morning, as hour 30 of the day it opened (T4). */
export const LATEST_CLOSE = 30;

/**
 * Where the venue's day has to start for this week (thai-fit T4): as far past midnight as its
 * latest night runs, and never earlier than where it already starts — the server only lets the
 * line rise, so Friday's 01:00 already sold stays Friday's.
 */
export function dayStartFor(week: readonly OpeningHoursDay[], startsNow = 0): number {
  return Math.max(startsNow, ...week.map((day) => (day.closesHour ?? 0) - 24));
}

/** The days that would open before the night before has finished (the server refuses them too). */
export function opensTooEarly(week: readonly OpeningHoursDay[], startsNow = 0): boolean {
  const line = dayStartFor(week, startsNow);
  return week.some((day) => day.opensHour !== null && day.opensHour < line);
}

/**
 * The price list with a price for every hour the new week opens (artboard c): the server refuses
 * to open an hour nobody has priced, and the price grid only paints hours that are open — so an
 * hour opened here takes the price of its nearest priced hour that day (else the day's cheapest
 * anywhere in the week), and the owner repaints it afterwards if they want another.
 *
 * Answers the bands as they would be saved and how many hours were added; null when the venue has
 * no prices at all, which is a price list to paint first rather than one to guess.
 */
export function pricesCovering(
  bands: readonly PriceBand[],
  week: readonly OpeningHoursDay[],
): { bands: PriceBand[]; added: number } | null {
  if (bands.length === 0) {
    return null;
  }

  const priceAt = (day: string, hour: number) =>
    bands.find((band) => band.day === day && band.fromHour <= hour && hour < band.toHour)
      ?.bahtPerHour ?? null;
  const cheapest = Math.min(...bands.map((band) => band.bahtPerHour));

  const added: PriceBand[] = [];
  for (const day of week) {
    if (day.opensHour === null || day.closesHour === null) {
      continue;
    }
    for (let hour = day.opensHour; hour < day.closesHour; hour++) {
      if (priceAt(day.day, hour) !== null) {
        continue;
      }
      let price: number | null = null;
      for (let step = 1; step < LATEST_CLOSE && price === null; step++) {
        price = priceAt(day.day, hour - step) ?? priceAt(day.day, hour + step);
      }
      added.push({
        day: day.day,
        fromHour: hour,
        toHour: hour + 1,
        bahtPerHour: price ?? cheapest,
      });
    }
  }

  return { bands: [...bands, ...added], added: added.length };
}
