import { Component, effect, inject, input, output, untracked } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { TranslationService } from '../../core/i18n/translation.service';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { provideLocalizedDateAdapter } from '../../shared/localized-date-adapter';

/**
 * Choosing which day's grid to look at (PRD US-02, S-04).
 *
 * Its own component because the calendar is the heaviest thing the grid page would otherwise
 * carry — a third of what a booker downloads before they can see a court — and it is only needed
 * by somebody who wants a different day. The grid defers it, so the hours arrive first and the
 * calendar follows (PRD 8's LCP target).
 */
@Component({
  selector: 'app-day-picker',
  imports: [ReactiveFormsModule, MatDatepickerModule, MatFormFieldModule, MatInputModule],
  providers: [FORM_FIELD_DEFAULTS, provideLocalizedDateAdapter()],
  template: `
    <mat-form-field class="field" data-testid="day-picker">
      <mat-label>{{ i18n.t('availability.day') }}</mat-label>
      <input
        id="day"
        matInput
        readonly
        [formControl]="field"
        [matDatepicker]="picker"
        [min]="min()"
        [max]="max()"
        (dateChange)="picked.emit($event.value)"
        (click)="picker.open()"
      />
      <mat-datepicker-toggle matIconSuffix [for]="picker" />
      <mat-datepicker #picker />
      <mat-hint>{{ i18n.t('availability.window') }}</mat-hint>
    </mat-form-field>
  `,
})
export class DayPicker {
  protected readonly i18n = inject(TranslationService);

  /** The day on screen. The address is what decides it, so it is only ever handed down. */
  readonly day = input.required<Date>();

  readonly min = input.required<Date>();

  /** The last day the server will take, which it states itself. */
  readonly max = input<Date | null>(null);

  readonly picked = output<Date | null>();

  protected readonly field = new FormControl<Date>(new Date(), { nonNullable: true });

  constructor() {
    // The field follows the day on screen, never the other way round: picking a day navigates,
    // and the page that comes back says which day that is.
    effect(() => {
      const day = this.day();
      untracked(() => this.field.setValue(day));
    });
  }
}
