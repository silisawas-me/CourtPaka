import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { forkJoin } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe, AppDateTimePipe } from '../../core/i18n/app-date.pipe';
import { plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { Court, CourtService, WEEKDAYS } from '../../core/venues/court.service';
import {
  AgreeSeriesRequest,
  BookingSeries,
  SeriesMiss,
  SeriesService,
} from '../../core/venues/series.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { provideLocalizedDateAdapter } from '../../shared/localized-date-adapter';

/** As much as the venue may write about standing a group down, and as much as the column holds. */
const NOTE_MAX_LENGTH = 500;

/** The hours a group can start and finish. Midnight at the end of a day is 24. */
const FIRST_HOUR = 0;
const LAST_HOUR = 24;

/**
 * The refusals a week can come back with, said as what they mean here. The booking codes are
 * written for somebody who just pressed a button — "somebody took it a moment ago" — and a week
 * a month away was not refused a moment ago, so it gets its own sentence (PRD US-23, US-30).
 */
const MISSED_BECAUSE = new Set(['booking.slot_just_taken', 'booking.hour_not_available']);

/**
 * The groups that come every week (PRD US-30).
 *
 * An arrangement reads as a sentence — this court, this weekday, these hours, from this date — so
 * the form is a sentence rather than a calendar: a calendar would ask which Tuesday, and the whole
 * point is that it is every Tuesday.
 *
 * The weeks that could not be made are what the page is really for. They are the phone calls
 * somebody has to make, so they sit on the arrangement's own row in the colour the app uses for
 * something waiting, rather than in a panel that has to be opened to be found.
 */
@Component({
  selector: 'app-series-page',
  imports: [
    ReactiveFormsModule,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    AppDatePipe,
    AppDateTimePipe,
  ],
  providers: [FORM_FIELD_DEFAULTS, provideLocalizedDateAdapter()],
  templateUrl: './series.page.html',
  styleUrl: './series.page.scss',
})
export class SeriesPage {
  private readonly series = inject(SeriesService);
  private readonly courts = inject(CourtService);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly noteMaxLength = NOTE_MAX_LENGTH;
  protected readonly firstHour = FIRST_HOUR;
  protected readonly lastHour = LAST_HOUR;
  protected readonly weekdays = WEEKDAYS;

  readonly venueId = input.required<string>();

  protected readonly courts$ = signal<Court[]>([]);
  protected readonly standing = signal<BookingSeries[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  protected readonly saving = signal(false);
  protected readonly saveError = signal<string | null>(null);

  /**
   * The arrangement being changed, or null when the form is agreeing a new one. The same form
   * either way: what is being described is the same thing, and a second form would be a second
   * set of fields to keep in step with it.
   */
  protected readonly changing = signal<BookingSeries | null>(null);

  /** How the last stop or change went, kept on screen because it is a count of cancellations. */
  protected readonly lastStop = signal<{ cancelled: number; left: number } | null>(null);

  /**
   * The arrangement the venue has pressed stop on, waiting to be confirmed. Stopping one cancels
   * every week of it still to come, and some of those may be owed money back (BR-06) — so it is
   * asked twice, and the reason is asked for while somebody still has it in mind (PRD US-30).
   */
  protected readonly stopping = signal<BookingSeries | null>(null);

  protected readonly stopNote = this.forms.nonNullable.control('', [
    Validators.maxLength(NOTE_MAX_LENGTH),
  ]);

  protected readonly running = computed(() =>
    this.standing().filter((one) => one.state === 'Running'),
  );

  protected readonly ended = computed(() => this.standing().filter((one) => one.state === 'Ended'));

  protected readonly nothingStanding = computed(
    () => !this.loading() && this.running().length === 0,
  );

  protected readonly form = this.forms.group({
    courtId: this.forms.control<string | null>(null, Validators.required),
    day: this.forms.nonNullable.control<string>(WEEKDAYS[0], Validators.required),
    fromHour: this.forms.nonNullable.control(18, Validators.required),
    untilHour: this.forms.nonNullable.control(20, Validators.required),
    customerName: this.forms.nonNullable.control('', [
      Validators.required,
      Validators.maxLength(200),
    ]),
    customerPhone: this.forms.nonNullable.control('', Validators.maxLength(20)),
    startsOn: this.forms.nonNullable.control(venueToday(), Validators.required),
  });

  constructor() {
    effect(() => this.load(this.venueId()));
  }

  /** How many weeks are waiting to be sorted out, across everything still running. */
  protected readonly weeksToSortOut = computed(() =>
    this.running().reduce((total, one) => total + one.missed.length, 0),
  );

  /** What to say about a week that could not be had. */
  protected why(miss: SeriesMiss): string {
    return MISSED_BECAUSE.has(miss.refusal)
      ? `series.missed.${miss.refusal}`
      : `error.${miss.refusal}`;
  }

  protected hours(one: BookingSeries): string {
    // Padded, because these sit under one another in a list and "6:00" under "18:00" reads as a
    // column that has slipped.
    return `${pad(one.fromHour)}:00 – ${pad(one.untilHour)}:00`;
  }

  /** Puts an arrangement's own terms in the form, so changing it starts from what it says. */
  protected startChanging(one: BookingSeries): void {
    this.changing.set(one);
    this.saveError.set(null);
    this.lastStop.set(null);

    this.form.patchValue({
      courtId: one.courtId,
      day: one.day,
      fromHour: one.fromHour,
      untilHour: one.untilHour,
      customerName: one.customerName,
      customerPhone: one.customerPhone ?? '',

      // From today, not from the arrangement's own first week: what is being asked is "from when
      // does this change", and the weeks before it stay as they were played.
      startsOn: venueToday(),
    });
  }

  protected stopChanging(): void {
    this.changing.set(null);
    this.saveError.set(null);
    this.form.reset({
      courtId: this.courts$().length === 1 ? this.courts$()[0].id : null,
      day: WEEKDAYS[0],
      fromHour: 18,
      untilHour: 20,
      customerName: '',
      customerPhone: '',
      startsOn: venueToday(),
    });
  }

  protected save(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.saving()) {
      return;
    }

    const asked = this.asked();
    const changing = this.changing();

    this.saving.set(true);
    this.saveError.set(null);
    this.lastStop.set(null);

    if (changing) {
      this.series.change(this.venueId(), changing.seriesId, asked).subscribe({
        next: (stopped) => {
          this.saving.set(false);
          this.lastStop.set({ cancelled: stopped.cancelled, left: stopped.left });
          this.stopChanging();

          // The one that was replaced has ended, and the list is the only thing that says so.
          this.reload();
        },
        error: (failure: unknown) => this.refused(failure),
      });

      return;
    }

    this.series.agree(this.venueId(), asked).subscribe({
      next: (agreed) => {
        this.saving.set(false);
        this.standing.update((all) => [agreed, ...all]);
        this.stopChanging();
      },
      error: (failure: unknown) => this.refused(failure),
    });
  }

  /** Asks before stopping: the answer cancels weeks of bookings and cannot be taken back. */
  protected askToStop(one: BookingSeries): void {
    this.stopping.set(one);
    this.stopNote.reset('');
    this.saveError.set(null);
    this.lastStop.set(null);
  }

  protected leaveItRunning(): void {
    this.stopping.set(null);
    this.stopNote.reset('');
  }

  protected stop(one: BookingSeries): void {
    if (this.saving() || this.stopNote.invalid) {
      return;
    }

    const note = this.stopNote.value.trim();

    this.saving.set(true);
    this.saveError.set(null);
    this.lastStop.set(null);

    this.series.stop(this.venueId(), one.seriesId, note === '' ? null : note).subscribe({
      next: (stopped) => {
        this.saving.set(false);
        this.leaveItRunning();
        this.lastStop.set({ cancelled: stopped.cancelled, left: stopped.left });
        this.standing.update((all) =>
          all.map((row) => (row.seriesId === stopped.series.seriesId ? stopped.series : row)),
        );
      },
      error: (failure: unknown) => this.refused(failure),
    });
  }

  private asked(): AgreeSeriesRequest {
    const { courtId, day, fromHour, untilHour, customerName, customerPhone, startsOn } =
      this.form.getRawValue();

    return {
      courtId: courtId!,
      day,
      fromHour,
      untilHour,
      customerName: customerName.trim(),
      customerPhone: customerPhone.trim() === '' ? null : customerPhone.trim(),
      startsOn: plainDate(startsOn),

      // No last week by default: a group that comes every Tuesday comes until they stop, and a
      // date the venue has to invent now is a date somebody has to remember to move later.
      untilOn: null,
    };
  }

  private refused(failure: unknown): void {
    this.saving.set(false);
    this.saveError.set(errorKey(failure));
  }

  private reload(): void {
    this.series.list(this.venueId()).subscribe({
      next: (all) => this.standing.set(all),
      error: (failure: unknown) => this.pageError.set(errorKey(failure)),
    });
  }

  private load(venueId: string): void {
    this.loading.set(true);
    this.pageError.set(null);

    forkJoin({
      courts: this.courts.courts(venueId),
      standing: this.series.list(venueId),
    }).subscribe({
      next: ({ courts, standing }) => {
        this.loading.set(false);
        this.courts$.set(courts);
        this.standing.set(standing);

        // One court is the common case, and choosing it is a step that answers itself.
        if (courts.length === 1) {
          this.form.patchValue({ courtId: courts[0].id });
        }
      },
      error: (failure: unknown) => {
        this.loading.set(false);
        this.pageError.set(errorKey(failure));
      },
    });
  }
}

function pad(hour: number): string {
  return hour.toString().padStart(2, '0');
}
