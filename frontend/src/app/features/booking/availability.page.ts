import { Component, computed, effect, inject, input, OnDestroy, signal } from '@angular/core';
import { FormBuilder, FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { DateAdapter, provideNativeDateAdapter } from '@angular/material/core';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { fromPlainDate, plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import {
  Availability,
  BOOKABLE_DAYS_AHEAD,
  PublicVenue,
  PublicVenueService,
} from '../../core/venues/public-venue.service';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

/** The grid follows the venue while it is on screen, because someone else may take an hour (US-02). */
const REFRESH_MS = 10_000;

/**
 * The court-by-hour grid for one day at one venue (PRD US-02). Anyone can read it; taking an hour
 * needs an account, which is what the button says when there is no session.
 */
@Component({
  selector: 'app-availability-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    MatButtonModule,
    MatCardModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    AppDatePipe,
  ],
  providers: [FORM_FIELD_DEFAULTS, provideNativeDateAdapter()],
  templateUrl: './availability.page.html',
  styleUrl: './availability.page.scss',
})
export class AvailabilityPage implements OnDestroy {
  private readonly venues = inject(PublicVenueService);
  private readonly auth = inject(AuthService);
  private readonly dates = inject(DateAdapter);

  protected readonly i18n = inject(TranslationService);
  protected readonly signedIn = computed(() => this.auth.currentUser() !== null);

  readonly venueId = input.required<string>();

  protected readonly today = signal(venueToday());
  protected readonly lastBookableDay = computed(() => {
    const last = new Date(this.today());
    last.setDate(last.getDate() + BOOKABLE_DAYS_AHEAD);
    return last;
  });

  protected readonly date: FormControl<Date> =
    inject(FormBuilder).nonNullable.control(venueToday());

  protected readonly venue = signal<PublicVenue | null>(null);
  protected readonly availability = signal<Availability | null>(null);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  /** The hours the venue is open that day, which are the columns of the grid. */
  protected readonly hours = computed(() => {
    const day = this.availability();
    return day?.opensHour === null || day?.closesHour == null
      ? []
      : Array.from(
          { length: day.closesHour - day.opensHour! },
          (_, index) => day.opensHour! + index,
        );
  });

  private refresh?: ReturnType<typeof setInterval>;

  constructor() {
    effect(() => this.dates.setLocale(this.i18n.locale()));

    // The venue is read when the route names a different one; the grid follows the day as well.
    effect(() => this.loadVenue(this.venueId()));
    effect(() => this.loadDay(this.venueId(), this.chosen()));

    this.refresh = setInterval(() => this.reload(), REFRESH_MS);
  }

  ngOnDestroy(): void {
    clearInterval(this.refresh);
  }

  /** The chosen day as a signal, so the effect above reruns when the picker changes. */
  private readonly chosen = signal(plainDate(venueToday()));

  protected pick(date: Date | null): void {
    if (date) {
      this.chosen.set(plainDate(date));
    }
  }

  private reload(): void {
    // A quiet refresh: the grid is replaced when the answer arrives, with no spinner in between.
    this.venues.availability(this.venueId(), this.chosen()).subscribe({
      next: (day) => this.availability.set(day),
      error: () => {
        // A refresh that fails leaves what is on screen; the next one will try again.
      },
    });
  }

  private loadVenue(venueId: string): void {
    this.venue.set(null);
    this.venues.venue(venueId).subscribe({
      next: (venue) => this.venue.set(venue),
      error: (failure: unknown) => this.pageError.set(errorKey(failure)),
    });
  }

  private loadDay(venueId: string, date: string): void {
    this.loading.set(true);
    this.pageError.set(null);

    this.venues.availability(venueId, date).subscribe({
      next: (day) => {
        this.availability.set(day);
        this.loading.set(false);
        this.date.setValue(fromPlainDate(day.date) ?? venueToday(), { emitEvent: false });
      },
      error: (failure: unknown) => {
        this.pageError.set(errorKey(failure));
        this.loading.set(false);
      },
    });
  }
}
