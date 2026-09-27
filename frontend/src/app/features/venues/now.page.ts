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
import { MatButton } from '@angular/material/button';
import { errorKey } from '../../core/http/api-error';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { venueNow } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { Availability, PublicVenueService } from '../../core/venues/public-venue.service';
import { VenueBooking, VenueBookingsService } from '../../core/venues/venue-bookings.service';
import { arriving, courtsNow, whoIs } from './now-board';
import { SellOntoBooking } from './sell-onto-booking';

/**
 * How often the floor is read again while the page is being looked at. The same idea as the
 * booker's grid (PRD US-02): a counter that left this open all evening should see a walk-in sold
 * at the other desk without anybody pressing anything.
 */
export const NOW_REFRESH_MS = 30_000;

/**
 * The counter's "now" (badPaka 2b): every court as it is this minute, who is due at the desk in
 * the next hour and a half, and a quick sale. Read from the same two answers the day console
 * reads — no endpoint of its own — so the two screens cannot disagree about a court.
 */
@Component({
  selector: 'app-now-page',
  imports: [MatButton, BahtPipe, SellOntoBooking],
  templateUrl: './now.page.html',
  styleUrl: './now.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NowPage {
  private readonly venues = inject(PublicVenueService);
  private readonly bookings = inject(VenueBookingsService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();

  private readonly clock = signal(venueNow());
  private readonly grid = signal<Availability | null>(null);
  private readonly day = signal<VenueBooking[] | null>(null);

  protected readonly pageError = signal<string | null>(null);
  protected readonly checkingIn = signal<string | null>(null);
  protected readonly checkInError = signal<string | null>(null);

  protected readonly time = computed(() => {
    const { hour, minute } = this.clock();
    return `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`;
  });

  protected readonly courts = computed(() => {
    const grid = this.grid();
    const day = this.day();
    return grid && day ? courtsNow(grid, day, this.clock()) : [];
  });

  protected readonly arriving = computed(() => arriving(this.day() ?? [], this.clock()));

  protected readonly counts = computed(() => {
    const courts = this.courts();
    return {
      playing: courts.filter((court) => court.state === 'playing').length,
      due: courts.filter((court) => court.state === 'due').length,
      free: courts.filter((court) => court.state === 'free').length,
    };
  });

  protected readonly who = whoIs;
  protected readonly round = Math.round;

  constructor() {
    effect(() => this.read(this.venueId(), { first: true }));

    // The clock moves the bars and the minutes; the floor is read again less often, and never
    // while nobody is looking — a hidden tab asking every thirty seconds is load for no reader.
    const tick = setInterval(() => {
      const before = this.clock().date;
      this.clock.set(venueNow());
      if (document.visibilityState !== 'visible') {
        return;
      }
      // Past midnight it is another day's floor, grid and all.
      this.read(this.venueId(), { first: this.clock().date !== before });
    }, NOW_REFRESH_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(tick));
  }

  protected checkIn(booking: VenueBooking): void {
    this.checkingIn.set(booking.bookingId);
    this.checkInError.set(null);
    this.bookings.checkIn(this.venueId(), booking.bookingId).subscribe({
      next: (changed) => {
        // The row the server answers with replaces its own, as on every other door.
        this.day.update((day) =>
          (day ?? []).map((one) => (one.bookingId === changed.bookingId ? changed : one)),
        );
        this.checkingIn.set(null);
      },
      error: (failure: unknown) => {
        this.checkInError.set(errorKey(failure));
        this.checkingIn.set(null);
      },
    });
  }

  private read(venueId: string, { first }: { first: boolean }): void {
    const date = this.clock().date;

    // The courts and the hours they sell do not change because somebody paid: asked once a day.
    // `refresh` so a counter watching its own floor is not counted as a booker looking at it.
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
      // A refresh that fails keeps the floor it had; only the first read says so.
      error: (failure: unknown) => {
        if (first) {
          this.pageError.set(errorKey(failure));
        }
      },
    });
  }
}
