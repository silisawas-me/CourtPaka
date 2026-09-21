/** The zone every date in this app means, whatever zone the viewer's browser is in (PRD BR-10). */
export const VENUE_TIME_ZONE = 'Asia/Bangkok';

/** en-CA writes YYYY-MM-DD, which is the shape the API speaks. */
const PLAIN_DATE = new Intl.DateTimeFormat('en-CA', {
  timeZone: VENUE_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
});

/**
 * The API speaks plain dates (YYYY-MM-DD) and the datepicker speaks Date, so the two meet here.
 *
 * The parts are read as they were picked: going through toISOString() would turn an evening into
 * the day before, which is how a week gets published a day early.
 */
export function plainDate(date: Date): string {
  return [
    date.getFullYear(),
    `${date.getMonth() + 1}`.padStart(2, '0'),
    `${date.getDate()}`.padStart(2, '0'),
  ].join('-');
}

/**
 * A plain date as a Date at local midnight. `new Date('2026-10-01')` would read it as midnight UTC,
 * which prints as the day before for anyone west of Greenwich.
 */
export function fromPlainDate(value: string | null | undefined): Date | null {
  const parts = value ? /^(\d{4})-(\d{2})-(\d{2})$/.exec(value.trim()) : null;
  if (!parts) {
    return null;
  }
  const [year, month, day] = [Number(parts[1]), Number(parts[2]) - 1, Number(parts[3])];
  const date = new Date(year, month, day);
  // Date rolls a 13th month into the next year rather than refusing it, so check it survived.
  return date.getFullYear() === year && date.getMonth() === month && date.getDate() === day
    ? date
    : null;
}

/**
 * Today where the venues are. The server validates against the Bangkok date, so a browser set to
 * another zone must not be offered a date the server will refuse — or be stopped from picking one
 * it would accept.
 */
export function venueToday(): Date {
  return fromPlainDate(PLAIN_DATE.format(new Date()))!;
}

const VENUE_CLOCK = new Intl.DateTimeFormat('en-GB', {
  timeZone: VENUE_TIME_ZONE,
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
});

/**
 * The time where the venues are, as the plain date and the hour and minute on a Bangkok wall.
 * Used to tell whether an hour is already over — the counter may sell one that has started but
 * not one that has ended (PRD US-13) — which a browser in another zone would otherwise judge by
 * its own clock.
 */
export function venueNow(at: Date = new Date()): { date: string; hour: number; minute: number } {
  const [hour, minute] = VENUE_CLOCK.format(at).split(':').map(Number);
  return { date: PLAIN_DATE.format(at), hour, minute };
}
