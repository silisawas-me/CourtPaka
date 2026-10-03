import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { forkJoin, Observable } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import { PackagesService, PackageType } from '../../core/venues/packages.service';
import { ShopItem, ShopService } from '../../core/venues/shop.service';

/**
 * What the venue sells besides the courts (thai-fit "แพ็กเกจ & สินค้า"): the board of hour
 * packages and the shop's items. Neither is edited — an offer or an item is taken off and a new
 * one put up, because what was sold already was sold on its old terms (BR-05). Stock comes in as
 * one expense that also fills the shelf (US-33), so the money and the count never part.
 */
@Component({
  selector: 'app-venue-catalog',
  imports: [BahtPipe],
  templateUrl: './venue-catalog.html',
  styleUrls: ['./venue-settings-tab.scss', './venue-catalog.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VenueCatalog {
  private readonly packages = inject(PackagesService);
  private readonly shop = inject(ShopService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  readonly canManage = input(false);

  protected readonly board = signal<PackageType[]>([]);
  protected readonly items = signal<ShopItem[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected readonly offers = computed(() => this.board().filter((one) => !one.withdrawnAt));
  protected readonly onShelf = computed(() => this.items().filter((one) => !one.withdrawnAt));

  /** A new offer on the board. */
  protected readonly offerName = signal('');
  protected readonly offerHours = signal('5');
  protected readonly offerPrice = signal('');
  protected readonly offerDays = signal('60');

  /** A new item on the shelf. */
  protected readonly itemName = signal('');
  protected readonly itemPrice = signal('');
  protected readonly itemUnit = signal('');
  protected readonly itemCounted = signal(true);

  /** Stock coming in: which item, how many, what it cost. */
  protected readonly restocking = signal<string | null>(null);
  protected readonly restockQty = signal('');
  protected readonly restockCost = signal('');

  constructor() {
    effect(() => this.read(this.venueId()));
  }

  protected addOffer(): void {
    const name = this.offerName().trim();
    const hours = Number(this.offerHours());
    const priceBaht = Number(this.offerPrice());
    const validForDays = Number(this.offerDays());
    if (!name || !(hours > 0) || !(priceBaht > 0) || !(validForDays > 0)) {
      return;
    }
    this.run(this.packages.offer(this.venueId(), { name, hours, priceBaht, validForDays }), () => {
      this.offerName.set('');
      this.offerPrice.set('');
    });
  }

  protected withdrawOffer(one: PackageType): void {
    this.run(this.packages.withdraw(this.venueId(), one.typeId));
  }

  protected addItem(): void {
    const name = this.itemName().trim();
    const priceBaht = Number(this.itemPrice());
    const unit = this.itemUnit().trim();
    if (!name || !(priceBaht > 0) || !unit) {
      return;
    }
    this.run(
      this.shop.addItem(this.venueId(), {
        name,
        priceBaht,
        unit,
        counted: this.itemCounted(),
        tellMeAt: null,
      }),
      () => {
        this.itemName.set('');
        this.itemPrice.set('');
        this.itemUnit.set('');
      },
    );
  }

  protected withdrawItem(one: ShopItem): void {
    this.run(this.shop.withdrawItem(this.venueId(), one.itemId));
  }

  protected openRestock(one: ShopItem): void {
    this.restocking.set(one.itemId);
    this.restockQty.set('');
    this.restockCost.set('');
  }

  /** One delivery: the expense and the stock row together, as the server insists (US-33). */
  protected restock(one: ShopItem): void {
    const quantity = Number(this.restockQty());
    const amountBaht = Number(this.restockCost());
    if (!(quantity > 0) || !(amountBaht > 0)) {
      return;
    }
    this.run(
      this.shop.spend(this.venueId(), {
        kind: 'Stock',
        amountBaht,
        paidOn: null,
        paidBy: 'Cash',
        note: one.name,
        itemId: one.itemId,
        quantity,
      }),
      () => this.restocking.set(null),
    );
  }

  private run(door: Observable<unknown>, after?: () => void): void {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    door.subscribe({
      next: () => {
        this.busy.set(false);
        after?.();
        this.read(this.venueId());
      },
      error: (failure: unknown) => {
        this.busy.set(false);
        this.error.set(errorKey(failure));
      },
    });
  }

  private read(venueId: string): void {
    forkJoin({ board: this.packages.board(venueId), items: this.shop.items(venueId) }).subscribe({
      next: ({ board, items }) => {
        this.board.set(board);
        this.items.set(items);
      },
      error: (failure: unknown) => this.error.set(errorKey(failure)),
    });
  }
}
