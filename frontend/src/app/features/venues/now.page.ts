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
import { WalkInEvents } from '../../core/venues/walk-in.events';
import { errorKey } from '../../core/http/api-error';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { venueNow } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { Availability, PublicVenueService } from '../../core/venues/public-venue.service';
import { ShopItem, ShopService } from '../../core/venues/shop.service';
import { VenueBooking, VenueBookingsService } from '../../core/venues/venue-bookings.service';
import { arriving, CourtNow, courtsNow, whoIs } from './now-board';
import { RouterLink } from '@angular/router';
import { PaymentMethod } from '../../core/venues/venue-bookings.service';

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
  imports: [BahtPipe, RouterLink],
  templateUrl: './now.page.html',
  styleUrl: './now.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NowPage {
  private readonly venues = inject(PublicVenueService);
  private readonly bookings = inject(VenueBookingsService);
  private readonly shop = inject(ShopService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();

  private readonly clock = signal(venueNow());
  private readonly grid = signal<Availability | null>(null);
  private readonly day = signal<VenueBooking[] | null>(null);

  protected readonly pageError = signal<string | null>(null);
  protected readonly checkingIn = signal<string | null>(null);
  protected readonly checkInError = signal<string | null>(null);

  /**
   * The quick sale, as the design draws it: tiles of what the shop sells. A tap puts one on the
   * counter (the tile says how many), and the row under the tiles takes the money in one press,
   * the same three ways the timeline's panel does. No booking: this is the drink somebody buys
   * on the way past.
   */
  protected readonly items = signal<ShopItem[]>([]);
  protected readonly counted = signal<Record<string, number>>({});
  protected readonly selling = signal(false);
  protected readonly sellError = signal<string | null>(null);
  protected readonly sold = signal(false);
  protected readonly payWith: readonly PaymentMethod[] = ['PromptPay', 'Card', 'Cash'];

  protected readonly quickTotal = computed(() =>
    this.items().reduce(
      (sum, item) => sum + item.priceBaht * (this.counted()[item.itemId] ?? 0),
      0,
    ),
  );

  protected readonly courts = computed(() => {
    const grid = this.grid();
    const day = this.day();
    return grid && day ? courtsNow(grid, day, this.clock()) : [];
  });

  protected readonly arriving = computed(() => arriving(this.day() ?? [], this.clock()));

  /** Somebody whose game has begun and who has not been taken in: the desk's first job. */
  protected readonly dueNow = computed(() =>
    this.courts().flatMap((court) =>
      court.state === 'due' && !this.arriving().some((one) => one.booking === court.booking)
        ? [court]
        : [],
    ),
  );

  /** Two rows of three as the design has it; more courts, more rows. */
  protected readonly rowsNeeded = computed(() => Math.max(2, Math.ceil(this.courts().length / 3)));

  protected readonly who = whoIs;
  protected readonly round = Math.round;

  constructor() {
    effect(() => this.read(this.venueId(), { first: true }));
    effect(() => {
      this.shop.items(this.venueId()).subscribe({
        next: (items) => this.items.set(items.filter((item) => !item.withdrawnAt)),
        error: () => this.items.set([]),
      });
    });

    // A walk-in sold from the top bar is on a court now (owner app PR-3).
    inject(WalkInEvents)
      .sold.pipe(takeUntilDestroyed())
      .subscribe(({ venueId }) => {
        if (venueId === this.venueId()) {
          this.read(venueId, { first: false });
        }
      });

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

  protected stateLabel(state: CourtNow['state']): string {
    return this.i18n.t(
      {
        playing: 'now.playing',
        due: 'now.notChecked',
        free: 'now.free',
        closed: 'now.shutCard',
        shut: 'now.outside',
      }[state],
    );
  }

  protected leftLabel(minutes: number): string {
    return this.i18n.t('now.leftMin').replace('{n}', String(minutes));
  }

  protected nextLabel(hour: number | null): string {
    return hour === null
      ? this.i18n.t('now.freeToClose')
      : this.i18n.t('now.nextAt').replace('{t}', `${hour}:00`);
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

  protected addOne(item: ShopItem): void {
    this.sold.set(false);
    this.sellError.set(null);
    this.counted.update((all) => ({ ...all, [item.itemId]: (all[item.itemId] ?? 0) + 1 }));
  }

  protected clearSale(): void {
    this.counted.set({});
    this.sellError.set(null);
  }

  protected sellNow(method: PaymentMethod): void {
    const lines = Object.entries(this.counted())
      .filter(([, quantity]) => quantity > 0)
      .map(([itemId, quantity]) => ({ itemId, quantity }));
    if (lines.length === 0 || this.selling()) {
      return;
    }
    this.selling.set(true);
    this.sellError.set(null);
    this.shop.sell(this.venueId(), { lines, paidBy: method, bookingId: null }).subscribe({
      next: () => {
        this.selling.set(false);
        this.counted.set({});
        this.sold.set(true);
      },
      error: (failure: unknown) => {
        this.selling.set(false);
        this.sellError.set(errorKey(failure));
      },
    });
  }
}
