import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { forkJoin } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import { CourtService, Weekday, WEEKDAYS } from '../../core/venues/court.service';
import { PricingService } from '../../core/venues/pricing.service';
import { Venue, VenueService } from '../../core/venues/venue.service';
import { bandsOf, gridOf, openHours, paint, PriceGrid } from './price-grid';
import { ClockPipe } from '../../core/i18n/clock.pipe';
import { VenueCatalog } from './venue-catalog';
import { VenueCourts } from './venue-courts';
import { VenueStaff } from './venue-staff';
import { VenuePolicy } from './venue-policy';
import { VenueHours } from './venue-hours';

/** How far one press of − or + moves a tier's price. */
const PRICE_STEP = 10;

/**
 * Pricing & peak, as the owner app paints it (PR-4): a few price tiers, and the week as a grid of
 * hours to paint them onto. Underneath it is the venue's price list — a day, a run of hours and
 * a price per band (O11) — so what is painted here is exactly what the booker is charged, and it
 * is saved the way prices always were: as a new version, the old one kept (BR-05).
 *
 * Only open hours take paint; a price for an hour the venue is shut would be a price nobody pays.
 */
@Component({
  selector: 'app-pricing-page',
  imports: [BahtPipe, ClockPipe, VenueHours, VenueCourts, VenueStaff, VenueCatalog, VenuePolicy],
  templateUrl: './pricing.page.html',
  styleUrl: './pricing.page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:pointerup)': 'painting.set(false)' },
})
export class PricingPage {
  private readonly pricing = inject(PricingService);
  private readonly courts = inject(CourtService);
  private readonly venues = inject(VenueService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();

  protected readonly days = WEEKDAYS;
  protected readonly grid = signal<PriceGrid | null>(null);
  protected readonly mine = signal<Venue[]>([]);
  protected readonly brush = signal(1);
  protected readonly painting = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly saved = signal(false);
  protected readonly saveError = signal<string | null>(null);
  /** Venues the prices were copied to, and any that refused them (with why). */
  protected readonly copied = signal<{ name: string; error: string | null }[]>([]);

  /** The section's tabs: prices and hours, then the rest of the venue's setup (thai-fit). */
  protected readonly tabs = ['prices', 'hours', 'courts', 'staff', 'catalog', 'policy'] as const;
  protected readonly tab = signal<(typeof this.tabs)[number]>('prices');

  /** Staff is the owner's alone (US-14): every door behind it is OwnerOnly at the server. */
  protected readonly shownTabs = computed(() =>
    this.tabs.filter((one) => one !== 'staff' || this.venue()?.role === 'Owner'),
  );

  /** A week saved on the hours tab opens or shuts hours, so the grid is read again. */
  protected reload(): void {
    this.load(this.venueId());
  }

  protected readonly venue = computed(
    () => this.mine().find((one) => one.id === this.venueId()) ?? null,
  );

  /** Whether this person may change prices here (US-14): the owner, or staff trusted with settings. */
  protected readonly canManage = computed(() => {
    const venue = this.venue();
    return (
      venue !== null && (venue.role === 'Owner' || venue.permissions.includes('ManageSettings'))
    );
  });

  /** The owner's other venues, which "apply to every branch" copies these prices to (O12). */
  protected readonly others = computed(() =>
    this.mine().filter((one) => one.id !== this.venueId() && one.role === 'Owner'),
  );

  constructor() {
    effect(() => this.load(this.venueId()));
  }

  protected tierName(tier: number, count: number): string {
    return count <= 3
      ? this.i18n.t(`pricing.tier.${tier}`)
      : `${this.i18n.t('pricing.tier.n')} ${tier + 1}`;
  }

  protected dayName(day: Weekday): string {
    return this.i18n.t(`pricing.day.${day}`);
  }

  protected nudge(tier: number, by: number): void {
    this.grid.update((grid) =>
      grid
        ? {
            ...grid,
            tiers: grid.tiers.map((price, index) =>
              index === tier ? Math.max(PRICE_STEP, price + by * PRICE_STEP) : price,
            ),
          }
        : grid,
    );
    this.saved.set(false);
  }

  protected press(day: Weekday, hour: number): void {
    this.painting.set(true);
    this.paintAt(day, hour);
  }

  /** Dragging across the grid paints what the pointer passes, as a brush does. */
  protected pass(day: Weekday, hour: number): void {
    if (this.painting()) {
      this.paintAt(day, hour);
    }
  }

  protected save(): void {
    const grid = this.grid();
    if (!grid || !this.canManage()) {
      return;
    }

    this.saving.set(true);
    this.saveError.set(null);
    this.copied.set([]);
    this.pricing.setPrices(this.venueId(), bandsOf(grid)).subscribe({
      next: () => {
        this.saving.set(false);
        this.saved.set(true);
      },
      error: (failure: unknown) => {
        this.saving.set(false);
        this.saveError.set(errorKey(failure));
      },
    });
  }

  /**
   * The same week at every venue this person owns. Each is saved on its own, because each is
   * checked against its own opening hours — a venue that opens earlier than these prices reach
   * refuses them, and says so, and the others still take them.
   */
  protected copyToAll(): void {
    const grid = this.grid();
    if (!grid) {
      return;
    }

    this.copied.set([]);
    for (const venue of this.others()) {
      this.pricing.setPrices(venue.id, bandsOf(grid)).subscribe({
        next: () => this.copied.update((all) => [...all, { name: venue.name, error: null }]),
        error: (failure: unknown) =>
          this.copied.update((all) => [...all, { name: venue.name, error: errorKey(failure) }]),
      });
    }
  }

  private paintAt(day: Weekday, hour: number): void {
    if (!this.canManage()) {
      return;
    }
    this.grid.update((grid) => (grid ? paint(grid, day, hour, this.brush()) : grid));
    this.saved.set(false);
  }

  private load(venueId: string): void {
    this.grid.set(null);
    this.loadError.set(null);
    forkJoin({
      prices: this.pricing.prices(venueId),
      weeks: this.courts.openingHours(venueId),
      mine: this.venues.mine(),
    }).subscribe({
      next: ({ prices, weeks, mine }) => {
        this.mine.set(mine);
        this.grid.set(gridOf(prices?.bands ?? [], openHours(weeks)));
      },
      error: (failure: unknown) => this.loadError.set(errorKey(failure)),
    });
  }
}
