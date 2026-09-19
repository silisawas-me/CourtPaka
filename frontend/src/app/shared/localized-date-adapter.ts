import { effect, inject, Injectable, Provider } from '@angular/core';
import { DateAdapter, provideNativeDateAdapter } from '@angular/material/core';
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
 * For a page with a datepicker. It is declared per page rather than globally so the adapter and the
 * calendar stay out of the bundle every other page pays for.
 */
export function provideLocalizedDateAdapter(): Provider[] {
  return [provideNativeDateAdapter(), { provide: DateAdapter, useClass: LocalizedDateAdapter }];
}
