import { Pipe, PipeTransform } from '@angular/core';
import { fromPlainDate } from './plain-date';

const FORMAT: Intl.DateTimeFormatOptions = { day: 'numeric', month: 'short', year: 'numeric' };

/** Building a formatter costs about thirty times what using one does, so each locale keeps its own. */
const FORMATTERS = new Map<string, Intl.DateTimeFormat>();

function formatter(locale: string): Intl.DateTimeFormat {
  let known = FORMATTERS.get(locale);
  if (!known) {
    known = new Intl.DateTimeFormat(locale, FORMAT);
    FORMATTERS.set(locale, known);
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
    return date === null ? '' : formatter(locale).format(date);
  }
}
