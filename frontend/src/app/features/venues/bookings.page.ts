import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { catchError, debounceTime, of, switchMap } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { fromPlainDate, plainDate, venueNow } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { PublicVenueService } from '../../core/venues/public-venue.service';
import { VenueBooking, VenueBookingsService } from '../../core/venues/venue-bookings.service';
import { WalkInEvents } from '../../core/venues/walk-in.events';
import {
  countsOf,
  courtsOf,
  dayOf,
  LIST_FILTERS,
  ListFilter,
  passes,
  stateOf,
  summaryOf,
  timeOf,
} from './booking-list';
import { BookingPanel, PanelCourt } from './booking-panel';
import { whoIs } from './now-board';

/** Typing pauses this long before the server is asked: one request a name, not one a letter. */
const SEARCH_AFTER_MS = 250;

/**
 * The schedule's third view (owner app, "รายการจอง"): one day's bookings as a list — any day,
 * with the day in the URL — narrowed by what still needs doing, and a search by name or phone
 * that looks across every day. Pressing a row opens the same panel the timeline does.
 */
@Component({
  selector: 'app-bookings-page',
  imports: [BahtPipe, BookingPanel],
  templateUrl: './bookings.page.html',
  styleUrl: './bookings.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BookingsPage {
  private readonly bookings = inject(VenueBookingsService);
  private readonly venues = inject(PublicVenueService);
  private readonly router = inject(Router);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  /** The day on screen, from `?date=`; today when there is none. */
  readonly date = input<string>();

  /** Where this venue's day starts, from its grid (thai-fit T4): 01:00 is yesterday's at 2. */
  private readonly dayStarts = signal(0);
  protected readonly today = computed(() => venueNow(new Date(), this.dayStarts()).date);
  protected readonly day = computed(() => this.date() ?? this.today());

  private readonly rows = signal<VenueBooking[] | null>(null);
  private readonly found = signal<VenueBooking[] | null>(null);
  protected readonly courts = signal<PanelCourt[]>([]);
  protected readonly pageError = signal<string | null>(null);

  protected readonly query = signal('');
  protected readonly filter = signal<ListFilter>('all');
  private readonly chosen = signal<string | null>(null);

  protected readonly filters = LIST_FILTERS;
  protected readonly who = whoIs;
  protected readonly timeOf = timeOf;
  protected readonly courtsOf = courtsOf;

  /** A search is on once enough is typed; it looks across every day, not the one on screen. */
  protected readonly searching = computed(() => this.query().trim().length >= 2);

  protected readonly loading = computed(() =>
    this.searching() ? this.found() === null : this.rows() === null,
  );

  private readonly base = computed(() => (this.searching() ? this.found() : this.rows()) ?? []);

  protected readonly counts = computed(() => countsOf(this.base()));
  protected readonly summary = computed(() => summaryOf(this.rows() ?? []));

  protected readonly shown = computed(() => {
    const now = Date.now();
    return this.base()
      .filter((one) => passes(one, this.filter()))
      .map((booking) => ({
        booking,
        state: stateOf(booking, now),
        day: dayOf(booking),
      }));
  });

  protected readonly selected = computed<VenueBooking | null>(() => {
    const id = this.chosen();
    return id ? (this.base().find((one) => one.bookingId === id) ?? null) : null;
  });

  /** "พฤ. 2 ต.ค. 2569": the day in the reader's language, Thai with the Buddhist year. */
  protected readonly dayLabel = computed(() => this.labelOf(this.day(), true));

  protected readonly isToday = computed(() => this.day() === this.today());
  protected readonly tomorrow = computed(() => shift(this.today(), 1));

  constructor() {
    effect(() => this.read(this.venueId(), this.day()));

    toObservable(computed(() => ({ venueId: this.venueId(), q: this.query().trim() })))
      .pipe(
        debounceTime(SEARCH_AFTER_MS),
        switchMap(({ venueId, q }) =>
          q.length < 2 ? of(null) : this.bookings.find(venueId, q).pipe(catchError(() => of([]))),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((found) => this.found.set(found));

    // A walk-in sold from the top bar lands on the list if it is the day on screen.
    inject(WalkInEvents)
      .sold.pipe(takeUntilDestroyed())
      .subscribe(({ venueId }) => {
        if (venueId === this.venueId()) {
          this.read(venueId, this.day());
        }
      });
  }

  protected labelOf(date: string, withYear = false): string {
    return new Intl.DateTimeFormat(this.i18n.locale(), {
      weekday: 'short',
      day: 'numeric',
      month: 'short',
      ...(withYear ? { year: 'numeric' } : {}),
    }).format(fromPlainDate(date) ?? new Date());
  }

  protected go(date: string): void {
    this.chosen.set(null);
    void this.router.navigate([], {
      queryParams: { date: date === this.today() ? null : date },
      queryParamsHandling: 'merge',
    });
  }

  protected walk(by: number): void {
    this.go(shift(this.day(), by));
  }

  protected choose(booking: VenueBooking): void {
    this.chosen.set(booking.bookingId);
    // On a phone the panel is under the list: bring it to where the finger is.
    if (window.matchMedia?.('(max-width: 59.99rem)').matches) {
      requestAnimationFrame(() =>
        document.querySelector('app-booking-panel')?.scrollIntoView({ block: 'start' }),
      );
    }
  }

  /** A door in the panel changed this booking: put the row in place wherever it is listed. */
  protected changed(changed: VenueBooking): void {
    const swap = (all: VenueBooking[] | null) =>
      all?.map((one) => (one.bookingId === changed.bookingId ? changed : one)) ?? null;
    this.rows.update(swap);
    this.found.update(swap);
  }

  protected refresh(): void {
    this.read(this.venueId(), this.day(), { quiet: true });
    const q = this.query().trim();
    if (q.length >= 2) {
      this.bookings.find(this.venueId(), q).subscribe((found) => this.found.set(found));
    }
  }

  private read(venueId: string, date: string, { quiet } = { quiet: false }): void {
    if (!quiet) {
      this.rows.set(null);
    }
    this.pageError.set(null);
    this.bookings.day(venueId, date).subscribe({
      next: (rows) => this.rows.set(rows),
      error: (failure: unknown) => {
        this.rows.set([]);
        this.pageError.set(errorKey(failure));
      },
    });
    this.venues.availability(venueId, date, true).subscribe({
      next: (grid) => {
        this.courts.set(grid.courts.map((court) => ({ courtId: court.courtId, name: court.name })));
        this.dayStarts.set(grid.dayStartsHour ?? 0);
      },
      error: () => this.courts.set([]),
    });
  }
}

/** A plain date some days away, counted on the calendar rather than in hours. */
function shift(date: string, days: number): string {
  const at = fromPlainDate(date) ?? new Date();
  return plainDate(new Date(at.getFullYear(), at.getMonth(), at.getDate() + days));
}
