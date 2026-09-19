import { Component, computed, effect, inject, input, signal } from '@angular/core';
import {
  FormArray,
  FormBuilder,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { CLOSING_HOURS, OPENING_HOURS, WEEKDAYS, Weekday } from '../../core/venues/court.service';
import { PriceBand, PricingService } from '../../core/venues/pricing.service';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

type BandForm = FormGroup<{
  day: FormControl<Weekday>;
  fromHour: FormControl<number>;
  toHour: FormControl<number>;
  bahtPerHour: FormControl<number>;
}>;

/**
 * What the venue charges per court-hour (PRD US-11). Published as a whole list: the server refuses
 * a set that leaves an open hour unpriced or prices one hour twice, so a half-edited list is never
 * what a booker sees.
 */
@Component({
  selector: 'app-price-bands',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './price-bands.html',
})
export class PriceBands {
  private readonly pricing = inject(PricingService);
  private readonly formBuilder = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly weekdays = WEEKDAYS;
  protected readonly fromHours = OPENING_HOURS;
  protected readonly toHours = CLOSING_HOURS;

  readonly venueId = input.required<string>();
  readonly canManage = input.required<boolean>();

  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly published = signal<PriceBand[]>([]);

  /** A venue reads its prices by day; the server already orders them by day and by hour. */
  protected readonly byDay = computed(() => {
    const bands = this.published();
    return WEEKDAYS.map((day) => ({
      day,
      bands: bands.filter((band) => band.day === day),
    })).filter((entry) => entry.bands.length > 0);
  });

  /** Angular has no [formArray] directive, so the array is bound through a group around it. */
  protected readonly form = this.formBuilder.group({
    bands: this.formBuilder.array<BandForm>([]),
  });

  protected get bands() {
    return this.form.controls.bands;
  }

  constructor() {
    effect(() => this.load(this.venueId()));
  }

  protected addBand(): void {
    this.bands.push(this.bandForm({ day: 'Monday', fromHour: 6, toHour: 22, bahtPerHour: 200 }));
  }

  protected removeBand(index: number): void {
    this.bands.removeAt(index);
  }

  protected save(): void {
    if (this.saving() || this.bands.length === 0) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);

    this.pricing.setPrices(this.venueId(), this.bands.getRawValue()).subscribe({
      next: (list) => {
        this.saving.set(false);
        this.published.set(list.bands);
      },
      error: (failure: unknown) => {
        this.saving.set(false);
        this.error.set(errorKey(failure));
      },
    });
  }

  private bandForm(band: PriceBand): BandForm {
    return this.formBuilder.nonNullable.group({
      day: this.formBuilder.nonNullable.control<Weekday>(band.day),
      fromHour: this.formBuilder.nonNullable.control(band.fromHour),
      toHour: this.formBuilder.nonNullable.control(band.toHour),
      bahtPerHour: this.formBuilder.nonNullable.control(band.bahtPerHour),
    });
  }

  private load(venueId: string): void {
    this.loading.set(true);
    this.error.set(null);
    this.published.set([]);
    this.bands.clear();

    this.pricing.prices(venueId).subscribe({
      next: (list) => {
        this.loading.set(false);
        this.published.set(list?.bands ?? []);
        // Editing starts from what is published, so a small change is a small edit.
        for (const band of list?.bands ?? []) {
          this.bands.push(this.bandForm(band));
        }
      },
      error: (failure: unknown) => {
        this.loading.set(false);
        this.error.set(errorKey(failure));
      },
    });
  }
}
