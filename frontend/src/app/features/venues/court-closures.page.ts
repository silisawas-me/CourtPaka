import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { ApiError, errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { ClashingBooking, ClosuresService, CourtClosure } from '../../core/venues/closures.service';
import { Court, CourtService } from '../../core/venues/court.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { provideLocalizedDateAdapter } from '../../shared/localized-date-adapter';

/** As much as the venue may write about why a court is shut, and as much as the column holds. */
const REASON_MAX_LENGTH = 200;

/** The hours a court can be shut from and until. Midnight at the end of a day is 24. */
const FIRST_HOUR = 0;
const LAST_HOUR = 24;

/**
 * Shutting a court for a stretch of time (PRD US-11).
 *
 * Its own page rather than a panel inside the venue's settings, because it is its own permission:
 * staff hold CloseCourt by default and ManageSettings by exception, so putting this behind the
 * settings screen would hide it from exactly the people who need it (PRD US-14).
 *
 * Whoever opens this is usually standing in front of a court that cannot be played on, so the
 * form is what the page opens with, and the answer that matters — the bookings already in that
 * stretch — appears directly under it rather than in a dialog over it.
 */
@Component({
  selector: 'app-court-closures-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    AppDatePipe,
  ],
  providers: [FORM_FIELD_DEFAULTS, provideLocalizedDateAdapter()],
  templateUrl: './court-closures.page.html',
  styleUrl: './court-closures.page.scss',
})
export class CourtClosuresPage {
  private readonly closures = inject(ClosuresService);
  private readonly courts = inject(CourtService);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly reasonMaxLength = REASON_MAX_LENGTH;

  // Typed rather than picked from a list: the app carries no select component, and adding one
  // for two fields would cost every page that loads the bundle (see the note in app.html).
  protected readonly firstHour = FIRST_HOUR;
  protected readonly lastHour = LAST_HOUR;

  readonly venueId = input.required<string>();

  protected readonly courts$ = signal<Court[]>([]);
  protected readonly closures$ = signal<CourtClosure[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  protected readonly saving = signal(false);
  protected readonly saveError = signal<string | null>(null);

  /**
   * The bookings the server refused the last attempt over. They are the whole point of the
   * refusal, so they stay on screen until the venue asks for something different.
   */
  protected readonly inTheWay = signal<ClashingBooking[]>([]);

  protected readonly nothingShut = computed(() => !this.loading() && this.closures$().length === 0);

  /**
   * A stretch of whole hours. The end is the first hour the court is back, which is how a single
   * evening hour and a fortnight end up the same shape.
   */
  protected readonly form = this.forms.group({
    courtId: this.forms.control<string | null>(null, Validators.required),
    startsOn: this.forms.nonNullable.control(venueToday(), Validators.required),
    startHour: this.forms.nonNullable.control(FIRST_HOUR, Validators.required),
    endsOn: this.forms.nonNullable.control(venueToday(), Validators.required),
    endHour: this.forms.nonNullable.control(LAST_HOUR, Validators.required),
    reason: this.forms.nonNullable.control('', [
      Validators.required,
      Validators.maxLength(REASON_MAX_LENGTH),
    ]),
  });

  constructor() {
    effect(() => this.load(this.venueId()));
  }

  protected courtName(courtId: string): string {
    return this.courts$().find((court) => court.id === courtId)?.name ?? '';
  }

  protected close(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.saving()) {
      return;
    }

    const { courtId, startsOn, startHour, endsOn, endHour, reason } = this.form.getRawValue();

    this.saving.set(true);
    this.saveError.set(null);
    this.inTheWay.set([]);

    this.closures
      .close(this.venueId(), courtId!, {
        startsOn: plainDate(startsOn),
        startHour,
        endsOn: plainDate(endsOn),
        endHour,
        reason: reason.trim(),
      })
      .subscribe({
        next: (closure) => {
          this.saving.set(false);
          this.closures$.update((shut) => [...shut, closure].sort(byStart));
          this.form.patchValue({ reason: '' });
          this.form.controls.reason.markAsUntouched();
        },
        error: (failure: unknown) => {
          this.saving.set(false);
          this.saveError.set(errorKey(failure));

          // The refusal hands back what is in the way, which is what the venue has to act on.
          if (failure instanceof ApiError && Array.isArray(failure.details?.['bookings'])) {
            this.inTheWay.set(failure.details['bookings'] as ClashingBooking[]);
          }
        },
      });
  }

  protected lift(closure: CourtClosure): void {
    if (this.saving()) {
      return;
    }

    this.saving.set(true);
    this.saveError.set(null);

    this.closures.lift(this.venueId(), closure.id).subscribe({
      next: (lifted) => {
        this.saving.set(false);
        this.closures$.update((shut) => shut.map((one) => (one.id === lifted.id ? lifted : one)));
      },
      error: (failure: unknown) => {
        this.saving.set(false);
        this.saveError.set(errorKey(failure));
      },
    });
  }

  private load(venueId: string): void {
    this.loading.set(true);
    this.pageError.set(null);

    forkJoin({
      courts: this.courts.courts(venueId),
      closures: this.closures.list(venueId),
    }).subscribe({
      next: ({ courts, closures }) => {
        this.loading.set(false);
        this.courts$.set(courts);
        this.closures$.set([...closures].sort(byStart));

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

/** Soonest first: the stretch being arranged now is the one at the top. */
function byStart(one: CourtClosure, other: CourtClosure): number {
  return (
    one.startsOn.localeCompare(other.startsOn) ||
    one.startHour - other.startHour ||
    one.id.localeCompare(other.id)
  );
}
