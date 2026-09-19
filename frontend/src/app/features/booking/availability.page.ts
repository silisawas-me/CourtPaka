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
import { Booking, BookingService } from '../../core/bookings/booking.service';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { fromPlainDate, plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { Availability, PublicVenueService } from '../../core/venues/public-venue.service';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { provideLocalizedDateAdapter } from '../../shared/localized-date-adapter';
import { VenueAddressPipe } from '../../shared/venue-address.pipe';

/** A court-hour the booker has picked, keyed the way the grid is. */
interface Picked {
  courtId: string;
  courtName: string;
  hour: number;
  bahtPerHour: number;
}

/**
 * The court-by-hour grid for one day at one venue, and the hours taken from it (PRD US-02, US-03).
 * Anyone can read it; taking an hour needs an account, which is what the button says when there is
 * no session.
 *
 * The day comes from the URL, so the page has one source of truth for what it shows, and a grid a
 * booker is looking at can be sent to whoever they are playing with.
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
    VenueAddressPipe,
  ],
  providers: [FORM_FIELD_DEFAULTS, provideLocalizedDateAdapter()],
  templateUrl: './availability.page.html',
  styleUrl: './availability.page.scss',
})
export class AvailabilityPage {
  private readonly venues = inject(PublicVenueService);
  private readonly bookings = inject(BookingService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly i18n = inject(TranslationService);
  protected readonly signedIn = computed(() => this.auth.currentUser() !== null);

  readonly venueId = input.required<string>();

  /** From ?date=; no date means today at the venue, which is where a booker starts. */
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

  /**
   * What the booker has picked, in the order they picked it. It is cleared when the day changes:
   * a booking is made from one grid, and carrying a pick across days would hide half of it.
   */
  protected readonly picked = signal<Picked[]>([]);

  protected readonly total = computed(() =>
    this.picked().reduce((sum, slot) => sum + slot.bahtPerHour, 0),
  );

  /** A held booking, once one has been made. Paying for it is US-04. */
  protected readonly held = signal<Booking | null>(null);
  protected readonly holding = signal(false);
  protected readonly bookingError = signal<string | null>(null);

  protected readonly picker: FormControl<Date> =
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
          this.clearPicks();
          // The field follows the URL, never the other way round: picking a day navigates.
          this.picker.setValue(fromPlainDate(this.chosen()) ?? venueToday());
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

  protected isPicked(courtId: string, hour: number): boolean {
    return this.picked().some((slot) => slot.courtId === courtId && slot.hour === hour);
  }

  /** Picking is a toggle, so the way to drop an hour is to touch it again. */
  protected toggle(court: { courtId: string; name: string }, hour: number, baht: number): void {
    this.bookingError.set(null);
    this.picked.update((picked) =>
      this.isPicked(court.courtId, hour)
        ? picked.filter((slot) => !(slot.courtId === court.courtId && slot.hour === hour))
        : [...picked, { courtId: court.courtId, courtName: court.name, hour, bahtPerHour: baht }],
    );
  }

  protected clearPicks(): void {
    this.picked.set([]);
    this.bookingError.set(null);
  }

  /**
   * Takes the hours. The grid may be minutes old, so the server prices and checks them again; a
   * refusal re-reads the day, because the reason is usually that the grid has moved on.
   */
  protected confirm(): void {
    const slots = this.picked();
    if (slots.length === 0 || this.holding()) {
      return;
    }

    this.holding.set(true);
    this.bookingError.set(null);

    this.bookings
      .hold(
        this.venueId(),
        slots.map((slot) => ({
          courtId: slot.courtId,
          date: this.chosen(),
          hour: slot.hour,
        })),
      )
      .subscribe({
        next: (booking) => {
          this.held.set(booking);
          this.picked.set([]);
          this.holding.set(false);
        },
        error: (failure: unknown) => {
          this.bookingError.set(errorKey(failure));
          this.holding.set(false);
          this.reread();
        },
      });
  }

  /** Reads the day again without touching the URL, after the server refused a pick. */
  private reread(): void {
    this.venues.availability(this.venueId(), this.chosen()).subscribe({
      next: (day) => this.day.set(day),
      error: () => {
        // The refusal already on screen is the more useful message; leave the grid alone.
      },
    });
  }
}
