import { effect, inject, Injectable, Provider } from '@angular/core';
import {
  DateAdapter,
  MAT_DATE_FORMATS,
  MAT_NATIVE_DATE_FORMATS,
  provideNativeDateAdapter,
} from '@angular/material/core';
import { NativeDateAdapter } from '@angular/material/core';
import { TranslationService } from '../core/i18n/translation.service';

/**
 * Material's date adapter, following the language the reader chose. The picker formats through
 * Intl, so this is what puts the calendar in Thai — including the Buddhist year.
 */
@Injectable()
export class LocalizedDateAdapter extends NativeDateAdapter {
  private readonly i18n = inject(TranslationService);

  constructor() {
    super();
    effect(() => this.setLocale(this.i18n.locale()));
  }
}

/**
 * A date in a picker's field is written the way every other date on screen is (AppDatePipe):
 * "23 ก.ย. 2569", not "23/9/2569". Material's own default is numeric, which left the same day
 * looking like two different ones depending on which part of the page it was in.
 */
const APP_DATE_FORMATS = {
  ...MAT_NATIVE_DATE_FORMATS,
  display: {
    ...MAT_NATIVE_DATE_FORMATS.display,
    dateInput: { day: 'numeric', month: 'short', year: 'numeric' } as Intl.DateTimeFormatOptions,
  },
};

/**
 * For a page with a datepicker. It is declared per page rather than globally so the adapter and the
 * calendar stay out of the bundle every other page pays for.
 */
export function provideLocalizedDateAdapter(): Provider[] {
  return [
    provideNativeDateAdapter(),
    { provide: DateAdapter, useClass: LocalizedDateAdapter },
    { provide: MAT_DATE_FORMATS, useValue: APP_DATE_FORMATS },
  ];
}
