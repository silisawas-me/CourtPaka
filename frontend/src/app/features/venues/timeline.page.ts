import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { errorKey } from '../../core/http/api-error';
import { venueNow } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { Availability, PublicVenueService } from '../../core/venues/public-venue.service';
import { VenueBooking, VenueBookingsService } from '../../core/venues/venue-bookings.service';
import { WalkInEvents } from '../../core/venues/walk-in.events';
import { BookingPanel } from './booking-panel';
import { NOW_REFRESH_MS } from './now.page';
import {
  dayHours,
  firstToShow,
  TIMELINE_HOURS,
  TimelineBlock,
  timelineRows,
  windowStart,
} from './timeline';

/**
 * The court schedule of one branch, as the owner app draws it: a track per court across nine
 * hours, a block per booking placed by the hour, the minute now as a line — and beside it, always
 * open, the booking the desk is dealing with: check in, one more hour, another court, shuttles
 * and drinks onto the bill, and what is left to take.
 *
 * The panel is `BookingPanel`, the same one the booking list opens; every door in it is the
 * server's (`can`, `GET …/hours`).
 */
@Component({
  selector: 'app-timeline-page',
  imports: [BookingPanel],
  templateUrl: './timeline.page.html',
  styleUrl: './timeline.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TimelinePage {
  private readonly venues = inject(PublicVenueService);
  private readonly bookings = inject(VenueBookingsService);
  private readonly walkIns = inject(WalkInEvents);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();

  private readonly clock = signal(venueNow());
  protected readonly grid = signal<Availability | null>(null);
  private readonly day = signal<VenueBooking[] | null>(null);

  /** Where the window starts once somebody walked it; until then it follows the clock. */
  private readonly walked = signal<number | null>(null);
  private readonly chosen = signal<string | null>(null);

  protected readonly pageError = signal<string | null>(null);

  private readonly allHours = computed(() => {
    const grid = this.grid();
    return grid ? dayHours(grid) : [];
  });

  protected readonly start = computed(
    () => this.walked() ?? windowStart(this.allHours(), this.clock().hour),
  );

  protected readonly hours = computed(() =>
    Array.from({ length: TIMELINE_HOURS }, (_, index) => this.start() + index),
  );

  protected readonly canWalkEarlier = computed(
    () => this.allHours().length > 0 && this.start() > this.allHours()[0],
  );
  protected readonly canWalkLater = computed(() => {
    const all = this.allHours();
    return all.length > 0 && this.start() + TIMELINE_HOURS < all[all.length - 1] + 1;
  });

  /** An hour is peak when it costs more than the cheapest hour of the day (the rust dot). */
  protected readonly peak = computed(() => {
    const prices = (this.grid()?.courts ?? []).flatMap((court) =>
      court.hours.filter((hour) => hour.bahtPerHour != null).map((hour) => hour),
    );
    const cheapest = Math.min(...prices.map((hour) => hour.bahtPerHour!));
    return new Set(prices.filter((hour) => hour.bahtPerHour! > cheapest).map((hour) => hour.hour));
  });

  protected readonly rows = computed(() => {
    const grid = this.grid();
    const day = this.day();
    return grid && day
      ? timelineRows(grid, day, this.start(), this.clock(), this.i18n.t('timeline.shut'))
      : [];
  });

  /** The minute now, as a place on the tracks; null when now is outside the window. */
  protected readonly nowAt = computed(() => {
    const { hour, minute } = this.clock();
    const at = ((hour + minute / 60 - this.start()) / TIMELINE_HOURS) * 100;
    return at < 0 || at > 100 ? null : at;
  });

  protected readonly nowLabel = computed(() => {
    const { hour, minute } = this.clock();
    return `${hour}:${String(minute).padStart(2, '0')}`;
  });

  protected readonly selected = computed<VenueBooking | null>(() => {
    const day = this.day() ?? [];
    const id = this.chosen();
    return (id ? day.find((one) => one.bookingId === id) : null) ?? firstToShow(day, this.clock());
  });

  constructor() {
    effect(() => this.read(this.venueId(), { first: true }));
    this.walkIns.sold.pipe(takeUntilDestroyed()).subscribe(({ venueId }) => {
      if (venueId === this.venueId()) {
        this.read(venueId, { first: true });
      }
    });

    const tick = setInterval(() => {
      const before = this.clock().date;
      this.clock.set(venueNow());
      if (document.visibilityState === 'visible') {
        this.read(this.venueId(), { first: this.clock().date !== before });
      }
    }, NOW_REFRESH_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(tick));
  }

  protected pick(block: TimelineBlock): void {
    if (block.booking) {
      this.chosen.set(block.booking.bookingId);
    }
  }

  /** A door in the panel changed this booking: put the row in place and stay on it. */
  protected changed(changed: VenueBooking): void {
    // Stay on it: a booking just cancelled is no longer the one the panel would open on its
    // own, and what it still owes back is the next thing to do (PRD US-18).
    this.chosen.set(changed.bookingId);
    this.day.update((day) =>
      (day ?? []).map((one) => (one.bookingId === changed.bookingId ? changed : one)),
    );
    // The grid of free courts changed too: a moved or longer booking holds other hours.
    this.read(this.venueId(), { first: true });
  }

  protected walk(by: number): void {
    const all = this.allHours();
    const last = all[all.length - 1] + 1 - TIMELINE_HOURS;
    this.walked.set(Math.max(all[0], Math.min(last, this.start() + by)));
  }

  /** A tap on an empty hour: the walk-in opens on that court and hour (the frame holds it). */
  protected bookAt(courtId: string, hour: number): void {
    this.walkIns.open.next({ venueId: this.venueId(), courtId, hour });
  }

  protected read(venueId: string, { first }: { first: boolean }): void {
    const date = this.clock().date;
    if (first) {
      this.venues.availability(venueId, date, true).subscribe({
        next: (grid) => this.grid.set(grid),
        error: (failure: unknown) => this.pageError.set(errorKey(failure)),
      });
    }
    this.bookings.day(venueId, date).subscribe({
      next: (day) => {
        this.day.set(day);
        this.pageError.set(null);
      },
      error: (failure: unknown) => {
        if (first) {
          this.pageError.set(errorKey(failure));
        }
      },
    });
  }
}
