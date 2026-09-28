import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { errorKey } from '../../core/http/api-error';
import { venueNow } from '../../core/i18n/plain-date';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import { OwnerToday, VenueService, VenueToday } from '../../core/venues/venue.service';

/**
 * Today at every venue this person reads the reports of — the owner app's first page, "the
 * schedule of every branch" (docs/plan/owner-app.md). Every number is the one the venue's own
 * dashboard would give, because the server reads each with the dashboard's code; this page is a
 * window onto those, not a second set of books.
 *
 * Drawn only when there is a venue whose reports this person may read: staff who take bookings
 * but do not read the money see nothing here, the same as on the dashboard (PRD US-14).
 */
/** As many hours as the design draws across the card. */
const HOURS_SHOWN = 9;

@Component({
  selector: 'app-all-venues-today',
  imports: [BahtPipe, RouterLink],
  templateUrl: './all-venues-today.html',
  styleUrl: './all-venues-today.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AllVenuesToday {
  private readonly venues = inject(VenueService);
  protected readonly i18n = inject(TranslationService);

  protected readonly today = signal<OwnerToday | null>(null);
  protected readonly error = signal<string | null>(null);

  /**
   * The rows of the grid: every branch that trades, and a branch not yet trading only when it has
   * hours on sale. A venue still applying has nothing to compare, and a row per application would
   * push the branches that do trade off the screen.
   */
  protected readonly rows = computed(() =>
    (this.today()?.venues ?? []).filter(
      (venue) =>
        venue.status === 'Approved' || venue.status === 'Suspended' || venue.sellableHours > 0,
    ),
  );

  /** Every hour any venue sells today, earliest opening to latest close. */
  private readonly allHours = computed(() => {
    const all = (this.today()?.venues ?? []).flatMap((venue) => venue.hours.map((h) => h.hour));
    if (all.length === 0) {
      return [];
    }
    const first = Math.min(...all);
    return Array.from({ length: Math.max(...all) - first + 1 }, (_, index) => first + index);
  });

  /** Where the window starts, once somebody has moved it; until then it follows the clock. */
  private readonly start = signal<number | null>(null);

  /**
   * The hours drawn: a window of nine, as the design has it, so a cell is wide enough to read at
   * a glance — starting two hours before now, so what just happened and the evening ahead are
   * both in it. ‹ and › walk it along the day.
   */
  protected readonly hours = computed(() => {
    const all = this.allHours();
    if (all.length <= HOURS_SHOWN) {
      return all;
    }
    return all.slice(this.from(), this.from() + HOURS_SHOWN);
  });

  protected readonly canGoEarlier = computed(() => this.from() > 0);
  protected readonly canGoLater = computed(
    () => this.from() + HOURS_SHOWN < this.allHours().length,
  );

  private readonly from = computed(() => {
    const all = this.allHours();
    const last = Math.max(0, all.length - HOURS_SHOWN);
    const chosen = this.start() ?? all.indexOf(venueNow().hour - 2);
    return Math.min(
      last,
      Math.max(0, chosen === -1 ? (venueNow().hour < all[0] ? 0 : last) : chosen),
    );
  });

  protected earlier(): void {
    this.start.set(Math.max(0, this.from() - 3));
  }

  protected later(): void {
    this.start.set(Math.min(this.allHours().length - HOURS_SHOWN, this.from() + 3));
  }

  /** Up or down on the same weekday last week, whole percent; null when that day had nothing. */
  protected readonly change = computed(() => {
    const day = this.today();
    return day && day.lastWeekKeptBaht > 0
      ? Math.round(((day.keptBaht - day.lastWeekKeptBaht) / day.lastWeekKeptBaht) * 100)
      : null;
  });

  /** "from Wednesday last week", named from the day the server says today is. */
  protected readonly lastWeek = computed(() => {
    const date = this.today()?.date;
    const weekday = date ? new Date(`${date}T00:00:00Z`).getUTCDay() : 0;
    return this.i18n
      .t('overview.vsLastWeek')
      .replace('{day}', this.i18n.t(`overview.weekday.${weekday}`));
  });

  /** Today's bookings by kind, as the design's note has them: "Walk-in 38 · ก๊วน 21". */
  protected readonly kinds = computed(() => {
    const all = this.today()?.byKind ?? [];
    return all.length === 0
      ? this.i18n.t('overview.noBookings')
      : all.map((one) => `${this.i18n.t('overview.kind.' + one.kind)} ${one.count}`).join(' · ');
  });

  /** Bookings past the venue's grace, across every venue — the note under "waiting now". */
  protected readonly late = computed(() =>
    (this.today()?.venues ?? []).reduce((sum, venue) => sum + venue.pastGrace, 0),
  );

  /**
   * What wants somebody, in a line under the grid as the design has it: courts shut for repair,
   * and bookings past the grace. Said per venue, because the owner acts on a venue, not a total.
   */
  protected readonly alerts = computed(() =>
    (this.today()?.venues ?? []).flatMap((venue) => [
      ...(venue.shutNow.length > 0
        ? [
            {
              testId: `overview-shut-${venue.venueId}`,
              text: `${venue.name} ${venue.shutNow.join(', ')} ${this.i18n.t('overview.shut')}`,
            },
          ]
        : []),
      ...(venue.pastGrace > 0
        ? [
            {
              testId: `overview-late-${venue.venueId}`,
              text: `${venue.name} ${this.i18n.t('overview.lateAt')} ${venue.pastGrace} ${this.i18n.t('overview.items')}`,
            },
          ]
        : []),
    ]),
  );

  constructor() {
    this.venues.today().subscribe({
      next: (today) => this.today.set(today),
      error: (failure: unknown) => this.error.set(errorKey(failure)),
    });
  }

  /** How full an hour was at a venue, or null when that venue did not sell it. */
  protected cell(venue: VenueToday, hour: number): { percent: number } | null {
    const found = venue.hours.find((one) => one.hour === hour);
    return !found || found.sellable === 0
      ? null
      : { percent: Math.round((100 * found.booked) / found.sellable) };
  }

  /** The courts a venue had on sale today — the most in any one hour. */
  protected courtsOf(venue: VenueToday): number {
    return Math.max(0, ...venue.hours.map((hour) => hour.sellable));
  }
}
