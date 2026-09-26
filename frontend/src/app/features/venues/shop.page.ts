import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { forkJoin, Observable } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import {
  SHOP_SPEND_KINDS,
  ShopItem,
  ShopSale,
  ShopService,
  Spend,
} from '../../core/venues/shop.service';
import { PAYMENT_METHODS } from '../../core/venues/venue-bookings.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

const NAME_MAX_LENGTH = 100;
const UNIT_MAX_LENGTH = 30;

/** One line of the board with how many of it are being rung up right now. */
interface Ringing {
  item: ShopItem;
  quantity: number;

  /** Never more than there are: the server refuses the rest, and a counter cannot hand it over. */
  most: number;
}

/**
 * What the counter sells besides court time, and what the venue paid out (PRD US-32, US-33).
 *
 * Four things in the order a shift uses them: ringing something up, what was sold, what was paid
 * out, and the board — which is a setting and folds away until somebody asks for it.
 *
 * The number that matters on a line is how many are left, so it is the one that reads first, and
 * the ones running low carry the colour the app uses for something waiting on a person, because
 * that is what they are: an order somebody has to place.
 */
@Component({
  selector: 'app-shop-page',
  imports: [
    ReactiveFormsModule,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    AppDatePipe,
    BahtPipe,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './shop.page.html',
  styleUrl: './shop.page.scss',
})
export class ShopPage {
  private readonly shop = inject(ShopService);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly methods = PAYMENT_METHODS;
  protected readonly kinds = SHOP_SPEND_KINDS;
  protected readonly nameMaxLength = NAME_MAX_LENGTH;
  protected readonly unitMaxLength = UNIT_MAX_LENGTH;

  readonly venueId = input.required<string>();

  protected readonly items = signal<ShopItem[]>([]);
  protected readonly sales = signal<ShopSale[]>([]);
  protected readonly spending = signal<Spend[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  protected readonly saving = signal(false);
  protected readonly saveError = signal<string | null>(null);

  protected readonly editingBoard = signal(false);

  /** The line whose shelf is being counted, if one is. */
  protected readonly counting = signal<ShopItem | null>(null);

  /** How many of each thing are being rung up right now, by item id. */
  private readonly basket = signal<Record<string, number>>({});

  protected readonly onSale = computed(() =>
    this.items().filter((one) => one.withdrawnAt === null),
  );

  protected readonly runningLow = computed(() => this.onSale().filter((one) => one.runningLow));

  /**
   * One row per line on the board, carrying what is being rung up and the most that can be. Built
   * once rather than asked per line: the template reads all three on every row, and Material runs
   * change detection on every press.
   */
  protected readonly ringing = computed<Ringing[]>(() =>
    this.onSale().map((item) => ({
      item,
      quantity: this.basket()[item.itemId] ?? 0,
      most: item.counted ? (item.left ?? 0) : Number.MAX_SAFE_INTEGER,
    })),
  );

  protected readonly basketLines = computed(() =>
    this.ringing().filter((line) => line.quantity > 0),
  );

  protected readonly basketTotal = computed(() =>
    this.basketLines().reduce((sum, line) => sum + line.item.priceBaht * line.quantity, 0),
  );

  protected readonly spentThisMonth = computed(() =>
    this.spending()
      .filter((one) => one.voidedAt === null)
      .reduce((sum, one) => sum + one.amountBaht, 0),
  );

  protected readonly soldToday = computed(() =>
    this.sales()
      .filter((one) => one.cancelledAt === null)
      .reduce((sum, one) => sum + one.totalBaht, 0),
  );

  protected readonly paidBy = this.forms.nonNullable.control<string>(PAYMENT_METHODS[0]);

  /** Money going out, which is its own thing and not a sale in reverse. */
  protected readonly spend = this.forms.nonNullable.group({
    kind: [SHOP_SPEND_KINDS[1] as string, Validators.required],
    amountBaht: [0, [Validators.required, Validators.min(0.01)]],
    paidBy: [PAYMENT_METHODS[0] as string, Validators.required],
    note: ['', Validators.maxLength(400)],

    // A delivery is one expense that also fills a shelf, so it is said once, here, rather than
    // written down twice and hoped to agree (PRD US-33).
    itemId: [''],
    quantity: [0, Validators.min(0)],
  });

  /** A line for the board. */
  protected readonly item = this.forms.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(NAME_MAX_LENGTH)]],
    priceBaht: [90, [Validators.required, Validators.min(0.01)]],
    unit: ['', [Validators.required, Validators.maxLength(UNIT_MAX_LENGTH)]],
    counted: [true],
    tellMeAt: [3, Validators.min(0)],
  });

  /** What the shelf actually holds, and why the ledger says otherwise. */
  protected readonly count = this.forms.nonNullable.group({
    counted: [0, [Validators.required, Validators.min(0)]],
    reason: ['', [Validators.required, Validators.maxLength(200)]],
  });

  /** Buying stock is only offered against something the venue counts. */
  protected readonly countedItems = computed(() => this.onSale().filter((one) => one.counted));

  constructor() {
    effect(() => this.load(this.venueId()));
  }

  protected add(line: Ringing, by: number): void {
    this.basket.update((basket) => ({
      ...basket,
      [line.item.itemId]: Math.min(Math.max(0, line.quantity + by), line.most),
    }));
  }

  protected clearBasket(): void {
    this.basket.set({});
  }

  protected sell(): void {
    const lines = this.basketLines();
    if (lines.length === 0) {
      return;
    }

    this.act(
      this.shop.sell(this.venueId(), {
        lines: lines.map((line) => ({ itemId: line.item.itemId, quantity: line.quantity })),
        paidBy: this.paidBy.value,
        bookingId: null,
      }),
      (sale) => {
        this.clearBasket();
        this.sales.update((all) => [sale, ...all]);

        // What is left has changed, and the counter is about to ring up the next thing. The rows
        // say by how much, so the shelf is not worth a round trip to be told.
        this.shelved(sale, -1);
      },
    );
  }

  protected cancelSale(sale: ShopSale): void {
    this.act(
      this.shop.cancelSale(this.venueId(), sale.saleId, this.i18n.t('shop.takenBack')),
      (taken) => {
        this.sales.update((all) => all.map((one) => (one.saleId === taken.saleId ? taken : one)));
        this.shelved(taken, 1);
      },
    );
  }

  protected recordSpend(): void {
    this.spend.markAllAsTouched();
    if (this.spend.invalid) {
      return;
    }

    const { kind, amountBaht, paidBy, note, itemId, quantity } = this.spend.getRawValue();
    const bought = kind === 'Stock' && itemId !== '' && quantity > 0;

    this.act(
      this.shop.spend(this.venueId(), {
        kind,
        amountBaht,

        // The server owns what day it is: a screen left open overnight would answer yesterday.
        paidOn: null,
        paidBy,
        note: note.trim() === '' ? null : note.trim(),
        itemId: bought ? itemId : null,
        quantity: bought ? quantity : null,
      }),
      (recorded) => {
        this.spending.update((all) => [recorded, ...all]);
        this.spend.patchValue({ amountBaht: 0, note: '', itemId: '', quantity: 0 });

        if (bought) {
          this.onTheShelf(itemId, quantity);
        }
      },
    );
  }

  protected voidSpend(one: Spend): void {
    this.act(
      this.shop.voidSpend(this.venueId(), one.spendId, this.i18n.t('shop.keyedWrong')),
      (voided) =>
        this.spending.update((all) =>
          all.map((row) => (row.spendId === voided.spendId ? voided : row)),
        ),
    );
  }

  protected startCount(one: ShopItem): void {
    this.counting.set(one);
    this.count.reset({ counted: one.left ?? 0, reason: '' });
  }

  protected saveCount(): void {
    const one = this.counting();
    this.count.markAllAsTouched();
    if (one === null || this.count.invalid) {
      return;
    }

    const { counted, reason } = this.count.getRawValue();

    this.act(this.shop.count(this.venueId(), one.itemId, counted, reason.trim()), (told) => {
      this.counting.set(null);
      this.items.update((all) => all.map((row) => (row.itemId === told.itemId ? told : row)));
    });
  }

  protected addItem(): void {
    this.item.markAllAsTouched();
    if (this.item.invalid) {
      return;
    }

    const asked = this.item.getRawValue();

    this.act(
      this.shop.addItem(this.venueId(), {
        name: asked.name.trim(),
        priceBaht: asked.priceBaht,
        unit: asked.unit.trim(),
        counted: asked.counted,
        tellMeAt: asked.counted ? asked.tellMeAt : null,
      }),
      (added) => {
        this.items.update((all) => [...all, added]);
        this.item.patchValue({ name: '', unit: '' });
        this.item.controls.name.markAsUntouched();
        this.item.controls.unit.markAsUntouched();
      },
    );
  }

  protected withdrawItem(one: ShopItem): void {
    this.act(this.shop.withdrawItem(this.venueId(), one.itemId), (gone) =>
      this.items.update((all) => all.map((row) => (row.itemId === gone.itemId ? gone : row))),
    );
  }

  /**
   * One shape for every button on this page: nothing while something else is in flight, the error
   * cleared before the attempt and set from the refusal, and the row patched on the way back.
   */
  private act<T>(call: Observable<T>, done: (value: T) => void): void {
    if (this.saving()) {
      return;
    }

    this.saving.set(true);
    this.saveError.set(null);

    call.subscribe({
      next: (value) => {
        this.saving.set(false);
        done(value);
      },
      error: (failure: unknown) => {
        this.saving.set(false);
        this.saveError.set(errorKey(failure));
      },
    });
  }

  /** Moves the shelf by a sale's lines: off it when sold, back on when the sale is taken back. */
  private shelved(sale: ShopSale, direction: 1 | -1): void {
    for (const line of sale.lines) {
      this.onTheShelf(line.itemId, direction * line.quantity);
    }
  }

  /**
   * How many are left, moved by what just happened. The server answers the same thing on the next
   * read; this is so the counter's next press is against the right number without waiting for it.
   */
  private onTheShelf(itemId: string, by: number): void {
    this.items.update((all) =>
      all.map((one) => {
        if (one.itemId !== itemId || !one.counted) {
          return one;
        }

        const left = Math.max(0, (one.left ?? 0) + by);
        return { ...one, left, runningLow: one.tellMeAt !== null && left <= one.tellMeAt };
      }),
    );
  }

  private load(venueId: string): void {
    this.loading.set(true);
    this.pageError.set(null);

    forkJoin({
      items: this.shop.items(venueId),
      sales: this.shop.sales(venueId),
      spending: this.shop.spending(venueId),
    }).subscribe({
      next: ({ items, sales, spending }) => {
        this.loading.set(false);
        this.items.set(items);
        this.sales.set(sales);
        this.spending.set(spending);

        // Nothing to sell yet, so the thing to do first is put something on the board.
        this.editingBoard.set(items.filter((one) => one.withdrawnAt === null).length === 0);
      },
      error: (failure: unknown) => {
        this.loading.set(false);
        this.pageError.set(errorKey(failure));
      },
    });
  }
}
