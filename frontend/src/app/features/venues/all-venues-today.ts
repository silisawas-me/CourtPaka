import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ClockPipe } from '../../core/i18n/clock.pipe';
import { RouterLink } from '@angular/router';
import { errorKey } from '../../core/http/api-error';
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
@Component({
  selector: 'app-all-venues-today',
  imports: [BahtPipe, RouterLink, ClockPipe],
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

  /**
   * Every hour any venue sells today, earliest opening to latest close, one column each — the
   * whole day across the screen, so every branch's evening is in view without walking to it.
   */
  protected readonly hours = computed(() => {
    const all = (this.today()?.venues ?? []).flatMap((venue) => venue.hours.map((h) => h.hour));
    if (all.length === 0) {
      return [];
    }
    const first = Math.min(...all);
    return Array.from({ length: Math.max(...all) - first + 1 }, (_, index) => first + index);
  });

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
    // Only the two the design names: the ones that walk in and the standing groups.
    const all = (this.today()?.byKind ?? []).filter(
      (one) => one.kind === 'WalkIn' || one.kind === 'Series',
    );
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
