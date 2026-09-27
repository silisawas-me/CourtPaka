import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { ShopItem, ShopSale, ShopService } from '../../core/venues/shop.service';
import { PAYMENT_METHODS } from '../../core/venues/venue-bookings.service';

/**
 * Selling a tube of shuttles to the people on a court, from the panel their booking is open in
 * (badPaka 2a, PRD US-32). The sale is the shop's own — paid there and then, into the same till —
 * and only carries the booking so the venue can see later whose game it went to; it does not
 * change what the booking owes, because it is not part of the court's price.
 *
 * The board is not fetched until somebody asks for it: most bookings opened here are opened to
 * check somebody in, and that should not cost the shop's list on every press.
 */
@Component({
  selector: 'app-sell-onto-booking',
  imports: [MatButton, BahtPipe],
  templateUrl: './sell-onto-booking.html',
  styleUrl: './sell-onto-booking.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SellOntoBooking {
  private readonly shop = inject(ShopService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  readonly bookingId = input.required<string>();

  protected readonly methods = PAYMENT_METHODS;

  /** Null until asked for; then what is on sale and not sold out. */
  protected readonly items = signal<ShopItem[] | null>(null);
  protected readonly counts = signal<Record<string, number>>({});
  protected readonly paidBy = signal<string>(PAYMENT_METHODS[0]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly sold = signal<ShopSale | null>(null);

  protected readonly total = computed(() =>
    (this.items() ?? []).reduce(
      (sum, item) => sum + item.priceBaht * (this.counts()[item.itemId] ?? 0),
      0,
    ),
  );

  protected open(): void {
    this.busy.set(true);
    this.error.set(null);
    this.shop.items(this.venueId()).subscribe({
      next: (items) => {
        this.items.set(
          items.filter(
            (item) => item.withdrawnAt === null && (item.left === null || item.left > 0),
          ),
        );
        this.busy.set(false);
      },
      error: (failure: unknown) => {
        this.error.set(errorKey(failure));
        this.busy.set(false);
      },
    });
  }

  protected countOf(item: ShopItem): number {
    return this.counts()[item.itemId] ?? 0;
  }

  /** Never below none, never past what the shelf holds — the server says no to that anyway. */
  protected add(item: ShopItem, by: number): void {
    const most = item.left ?? Number.MAX_SAFE_INTEGER;
    this.counts.update((counts) => ({
      ...counts,
      [item.itemId]: Math.min(Math.max(0, this.countOf(item) + by), most),
    }));
    this.sold.set(null);
  }

  protected sell(): void {
    const lines = Object.entries(this.counts())
      .filter(([, quantity]) => quantity > 0)
      .map(([itemId, quantity]) => ({ itemId, quantity }));
    if (lines.length === 0) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.shop
      .sell(this.venueId(), { lines, paidBy: this.paidBy(), bookingId: this.bookingId() })
      .subscribe({
        next: (sale) => {
          this.sold.set(sale);
          this.counts.set({});
          // What is left on the shelf moved; the next press reads it again rather than guessing.
          this.items.set(null);
          this.busy.set(false);
        },
        error: (failure: unknown) => {
          this.error.set(errorKey(failure));
          this.busy.set(false);
        },
      });
  }
}
