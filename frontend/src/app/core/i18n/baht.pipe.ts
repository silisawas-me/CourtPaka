import { Pipe, PipeTransform } from '@angular/core';

/**
 * Money is written to the satang and no further; anything longer is a rounding error wearing a
 * decimal point.
 */
const FORMAT: Intl.NumberFormatOptions = { maximumFractionDigits: 2 };

/** Building a formatter costs about thirty times what using one does, so each locale keeps its own. */
const FORMATTERS = new Map<string, Intl.NumberFormat>();

function formatter(locale: string): Intl.NumberFormat {
  let known = FORMATTERS.get(locale);
  if (!known) {
    known = new Intl.NumberFormat(locale, FORMAT);
    FORMATTERS.set(locale, known);
  }
  return known;
}

/** The same formatting, for the few places that build a string rather than render a value. */
export function formatBaht(value: number, locale: string): string {
  return formatter(locale).format(value);
}

/**
 * An amount in the reader's own grouping, with no currency sign: every screen that shows money
 * says baht in its own label, and a sign repeated on thirty rows of a table is thirty times the
 * same word.
 *
 * The locale is passed in rather than injected, like {@link AppDatePipe}, so the pipe stays pure:
 * the template re-renders when the language signal changes and nothing else.
 */
@Pipe({ name: 'baht' })
export class BahtPipe implements PipeTransform {
  transform(value: number | null | undefined, locale: string): string {
    return value === null || value === undefined || Number.isNaN(value)
      ? ''
      : formatter(locale).format(value);
  }
}
