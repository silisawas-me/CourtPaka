import { Pipe, PipeTransform } from '@angular/core';
import { fromPlainDate } from './plain-date';

const FORMAT: Intl.DateTimeFormatOptions = { day: 'numeric', month: 'short', year: 'numeric' };

/** The same date, plus the time of day — for things that happened at a moment, not on a day. */
const WITH_TIME: Intl.DateTimeFormatOptions = {
  ...FORMAT,
  hour: '2-digit',
  minute: '2-digit',
};

/** Building a formatter costs about thirty times what using one does, so each locale keeps its own. */
const FORMATTERS = new Map<string, Intl.DateTimeFormat>();

function formatter(locale: string, options: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
  const key = options === WITH_TIME ? `${locale}+time` : locale;
  let known = FORMATTERS.get(key);
  if (!known) {
    known = new Intl.DateTimeFormat(locale, options);
    FORMATTERS.set(key, known);
  }
  return known;
}

/**
 * A plain date (YYYY-MM-DD, as the API writes them) the way the reader expects it. The locale is
 * passed in rather than injected so the pipe stays pure: the template re-renders when the language
 * signal changes, and nothing else.
 *
 * Angular's own DatePipe has no Buddhist era, and the Material datepicker formats through Intl, so
 * going through Intl here is what keeps a date on the page and a date in a picker reading the same.
 */
@Pipe({ name: 'appDate' })
export class AppDatePipe implements PipeTransform {
  transform(value: string, locale: string): string {
    const date = fromPlainDate(value);
    return date === null ? '' : formatter(locale, FORMAT).format(date);
  }
}

/**
 * A moment, as the reader expects it: the same date as {@link AppDatePipe} with the time beside
 * it. Separate from that pipe because the API answers two different shapes — a plain date for a
 * day a court is booked, and a full timestamp for something that happened — and reading one as
 * the other is how a date silently renders as nothing.
 */
@Pipe({ name: 'appDateTime' })
export class AppDateTimePipe implements PipeTransform {
  transform(value: string, locale: string): string {
    const at = new Date(value);
    return Number.isNaN(at.getTime()) ? '' : formatter(locale, WITH_TIME).format(at);
  }
}
