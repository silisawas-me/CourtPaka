import { Component, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { FormBuilder, FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { catchError, EMPTY, switchMap, tap } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { fromPlainDate, plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { Availability, PublicVenueService } from '../../core/venues/public-venue.service';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { provideLocalizedDateAdapter } from '../../shared/localized-date-adapter';
import { VenueAddressPipe } from '../../shared/venue-address.pipe';

/**
 * The court-by-hour grid for one day at one venue (PRD US-02). Anyone can read it; taking an hour
 * needs an account, which is what the button says when there is no session.
 *
 * The day comes from the URL, so the page has one source of truth for what it shows, and a grid a
 * booker is looking at can be sent to whoever they are playing with.
 */
@Component({
  selector: 'app-availability-page',
  imports: [
    VenueAddressPipe,
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
  providers: [FORM_FIELD_DEFAULTS, provideLocalizedDateAdapter()],
  templateUrl: './availability.page.html',
  styleUrl: './availability.page.scss',
})
export class AvailabilityPage {
  private readonly venues = inject(PublicVenueService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly i18n = inject(TranslationService);
  protected readonly signedIn = computed(() => this.auth.currentUser() !== null);

  readonly venueId = input.required<string>();

  /** From <c>?date=</c>; no date means today at the venue, which is where a booker starts. */
  readonly date = input<string>();

  protected readonly chosen = computed(() => this.date() ?? plainDate(venueToday()));

  protected readonly today = venueToday();

  protected readonly day = signal<Availability | null>(null);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  protected readonly venue = computed(() => this.day()?.venue ?? null);

  /** The server states the window, so the calendar cannot offer a day it would refuse. */
  protected readonly lastBookableDay = computed(() => fromPlainDate(this.day()?.lastBookableDate));

  /** The hours the venue is open that day, which are the columns of the grid. */
  protected readonly hours = computed(() => {
    const opens = this.day()?.opensHour;
    const closes = this.day()?.closesHour;
    return opens == null || closes == null
      ? []
      : Array.from({ length: closes - opens }, (_, index) => opens + index);
  });

  protected readonly picked: FormControl<Date> =
    inject(FormBuilder).nonNullable.control(venueToday());

  /** What the page is showing: the two halves of it that come from the route. */
  private readonly asked = computed(() => ({ venueId: this.venueId(), date: this.chosen() }));

  constructor() {
    // switchMap drops the answer to a day the booker has already moved off, so going back and
    // forward through the history cannot leave an older day's grid on screen.
    toObservable(this.asked)
      .pipe(
        tap(() => {
          this.loading.set(true);
          this.pageError.set(null);
          // The field follows the URL, never the other way round: picking a day navigates.
          this.picked.setValue(fromPlainDate(this.chosen()) ?? venueToday());
        }),
        switchMap(({ venueId, date }) =>
          this.venues.availability(venueId, date).pipe(
            catchError((failure: unknown) => {
              // Nothing of the day survives a failure; the error is what the page shows.
              this.day.set(null);
              this.pageError.set(errorKey(failure));
              this.loading.set(false);
              return EMPTY;
            }),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((day) => {
        this.day.set(day);
        this.loading.set(false);
      });
  }

  protected pick(date: Date | null): void {
    if (date) {
      void this.router.navigate([], {
        queryParams: { date: plainDate(date) },
        queryParamsHandling: 'merge',
      });
    }
  }
}
