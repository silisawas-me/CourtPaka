import { DOCUMENT } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import {
  catchError,
  EMPTY,
  exhaustMap,
  filter,
  fromEvent,
  interval,
  merge,
  switchMap,
  tap,
} from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { BookingService } from '../../core/bookings/booking.service';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { fromPlainDate, plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { Availability, PublicVenueService } from '../../core/venues/public-venue.service';
import { DayPicker } from './day-picker';
import { VenueAddressPipe } from '../../shared/venue-address.pipe';

/** A court-hour, keyed the way the grid is. */
function key(courtId: string, hour: number): string {
  return `${courtId}@${hour}`;
}

/** How often an open grid reads its day again (PRD US-02). */
export const REFRESH_EVERY_MS = 10_000;

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
    RouterLink,
    MatButtonModule,
    MatCardModule,
    DayPicker,
    MatProgressBarModule,
    AppDatePipe,
    VenueAddressPipe,
  ],
  templateUrl: './availability.page.html',
  styleUrl: './availability.page.scss',
})
export class AvailabilityPage {
  private readonly venues = inject(PublicVenueService);
  private readonly bookings = inject(BookingService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);

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
   * What the booker has picked, as keys into the grid rather than copies of it. The name and the
   * price are read back off the day that is on screen, so a grid re-read after a refusal cannot
   * leave the summary quoting a price the venue no longer charges.
   */
  private readonly picks = signal<readonly string[]>([]);

  /** The picked hours as the summary shows them, in the order they were picked. */
  protected readonly picked = computed(() => {
    const grid = this.day();
    return this.picks().flatMap((pick) => {
      const courtId = pick.slice(0, pick.lastIndexOf('@'));
      const hour = Number(pick.slice(pick.lastIndexOf('@') + 1));
      const court = grid?.courts.find((row) => row.courtId === courtId);
      const cell = court?.hours.find((slot) => slot.hour === hour);
      // An hour that stopped being free while it was picked is no longer picked: someone else
      // took it, or the venue closed it, and the summary says what can still be booked.
      return court && cell?.status === 'Free' && cell.bahtPerHour !== null
        ? [{ courtId, courtName: court.name, hour, bahtPerHour: cell.bahtPerHour }]
        : [];
    });
  });

  private readonly pickedKeys = computed(
    () => new Set(this.picked().map((slot) => key(slot.courtId, slot.hour))),
  );

  protected readonly total = computed(() =>
    this.picked().reduce((sum, slot) => sum + slot.bahtPerHour, 0),
  );

  protected readonly holding = signal(false);
  protected readonly bookingError = signal<string | null>(null);

  /** The day on screen as a date, which is what the picker and the placeholder both show. */
  protected readonly shownDay = computed(() => fromPlainDate(this.chosen()) ?? venueToday());

  /** Bumped to read the day again without changing what the page is showing. */
  private readonly refresh = signal(0);

  /** What the page is showing: the two halves of it that come from the route, and a retry. */
  private readonly asked = computed(() => ({
    venueId: this.venueId(),
    date: this.chosen(),
    attempt: this.refresh(),
  }));

  constructor() {
    // switchMap drops the answer to a day the booker has already moved off, so going back and
    // forward through the history cannot leave an older day's grid on screen.
    toObservable(this.asked)
      .pipe(
        tap(() => {
          this.loading.set(true);
          this.pageError.set(null);
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

    // While the page is open the day is read again every ten seconds, and at once when the tab
    // comes back into view (US-02). Quietly: no progress bar over a grid that is already there,
    // and a failed refresh keeps the grid that was — the booking itself is checked again on the
    // server whatever the grid says. The stream starts over with every day asked for, so a
    // refresh of the day the booker has just left can never land on the one they moved to.
    toObservable(this.asked)
      .pipe(
        switchMap(({ venueId, date }) =>
          merge(interval(REFRESH_EVERY_MS), fromEvent(this.document, 'visibilitychange')).pipe(
            filter(() => this.worthRefreshing()),
            exhaustMap(() =>
              this.venues.availability(venueId, date, true).pipe(catchError(() => EMPTY)),
            ),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((day) => {
        // Asked again on arrival: a hold made while this was on its way would come back as the
        // booker's own hours taken, and empty the summary under the button they just pressed.
        if (this.worthRefreshing()) {
          this.day.set(day);
        }
      });

    // A booking is made from one grid, so moving to another day starts the pick again.
    effect(() => {
      this.chosen();
      untracked(() => this.clearPicks());
    });
  }

  /**
   * A hidden tab is nobody looking, and while the day is loading or a hold is being made the
   * answer on its way is newer than a refresh would be.
   */
  private worthRefreshing(): boolean {
    return (
      this.document.visibilityState === 'visible' &&
      this.day() !== null &&
      !this.loading() &&
      !this.holding()
    );
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
    return this.pickedKeys().has(key(courtId, hour));
  }

  /** Picking is a toggle, so the way to drop an hour is to touch it again. */
  protected toggle(courtId: string, hour: number): void {
    this.bookingError.set(null);
    const picked = key(courtId, hour);
    this.picks.update((picks) =>
      picks.includes(picked) ? picks.filter((held) => held !== picked) : [...picks, picked],
    );
  }

  /** The accessible name of a cell, so the button and the plain hour read the same. */
  protected cellLabel(courtName: string, hour: number, status: string): string {
    return `${courtName} ${hour}:00 ${this.i18n.t('availability.status.' + status)}`;
  }

  protected clearPicks(): void {
    this.picks.set([]);
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
          this.picks.set([]);
          this.holding.set(false);
          // Paying for it is the next thing to do, and it is on a fifteen-minute clock.
          void this.router.navigate(['/bookings', booking.id]);
        },
        error: (failure: unknown) => {
          this.bookingError.set(errorKey(failure));
          this.holding.set(false);
          // The reason is usually that the grid has moved on, so read the day again — through the
          // one stream the page loads days with, not a second one beside it.
          this.refresh.update((attempt) => attempt + 1);
        },
      });
  }
}
