import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { forkJoin } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { ShopItem, ShopSale, ShopService, Spend } from '../../core/venues/shop.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

/** How money reaches or leaves the venue. The same three the till counts (PRD US-26). */
const METHODS = ['Cash', 'PromptPay', 'Card'] as const;

/** What a venue spends money on (PRD US-33). */
const KINDS = ['Stock', 'Utilities', 'Wages', 'Repairs', 'Other'] as const;

const NAME_MAX_LENGTH = 100;
const UNIT_MAX_LENGTH = 30;

/**
 * What the counter sells besides court time, and what the venue paid out (PRD US-32, US-33).
 *
 * Three things in the order a shift uses them: ringing something up, what has been paid out, and
 * the board — which is a setting and folds away until somebody asks for it.
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
  protected readonly methods = METHODS;
  protected readonly kinds = KINDS;
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

  /** How many of each thing are being rung up right now, by item id. */
  protected readonly basket = signal<Record<string, number>>({});

  protected readonly onSale = computed(() =>
    this.items().filter((one) => one.withdrawnAt === null),
  );

  protected readonly runningLow = computed(() => this.onSale().filter((one) => one.runningLow));

  protected readonly basketLines = computed(() =>
    this.onSale()
      .map((item) => ({ item, quantity: this.basket()[item.itemId] ?? 0 }))
      .filter((line) => line.quantity > 0),
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

  protected readonly nothingSold = computed(() => !this.loading() && this.sales().length === 0);

  protected readonly paidBy = this.forms.nonNullable.control<string>(METHODS[0]);

  /** Money going out, which is its own thing and not a sale in reverse. */
  protected readonly spend = this.forms.nonNullable.group({
    kind: [KINDS[1] as string, Validators.required],
    amountBaht: [0, [Validators.required, Validators.min(0.01)]],
    paidBy: [METHODS[0] as string, Validators.required],
    note: ['', Validators.maxLength(400)],
  });

  /** A line for the board. */
  protected readonly item = this.forms.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(NAME_MAX_LENGTH)]],
    priceBaht: [90, [Validators.required, Validators.min(0.01)]],
    unit: ['', [Validators.required, Validators.maxLength(UNIT_MAX_LENGTH)]],
    counted: [true],
    tellMeAt: [3, Validators.min(0)],
  });

  constructor() {
    effect(() => this.load(this.venueId()));
  }

  protected inBasket(itemId: string): number {
    return this.basket()[itemId] ?? 0;
  }

  protected add(item: ShopItem, by: number): void {
    this.basket.update((basket) => {
      const now = Math.max(0, (basket[itemId(item)] ?? 0) + by);

      // Never more than there are: the server refuses it, and a counter should not be able to
      // ring up what it cannot hand over (PRD US-32).
      const most = item.counted ? (item.left ?? 0) : Number.MAX_SAFE_INTEGER;
      return { ...basket, [itemId(item)]: Math.min(now, most) };
    });
  }

  protected clearBasket(): void {
    this.basket.set({});
  }

  protected sell(): void {
    const lines = this.basketLines();
    if (lines.length === 0 || this.saving()) {
      return;
    }

    this.saving.set(true);
    this.saveError.set(null);

    this.shop
      .sell(this.venueId(), {
        lines: lines.map((line) => ({ itemId: line.item.itemId, quantity: line.quantity })),
        paidBy: this.paidBy.value,
        bookingId: null,
      })
      .subscribe({
        next: (sale) => {
          this.saving.set(false);
          this.clearBasket();
          this.sales.update((all) => [sale, ...all]);

          // What is left has changed, and the counter is about to ring up the next thing.
          this.reloadItems();
        },
        error: (failure: unknown) => this.refused(failure),
      });
  }

  protected cancelSale(sale: ShopSale): void {
    if (this.saving()) {
      return;
    }

    this.saving.set(true);
    this.saveError.set(null);

    this.shop.cancelSale(this.venueId(), sale.saleId, this.i18n.t('shop.takenBack')).subscribe({
      next: (taken) => {
        this.saving.set(false);
        this.sales.update((all) => all.map((one) => (one.saleId === taken.saleId ? taken : one)));
        this.reloadItems();
        this.reloadSpending();
      },
      error: (failure: unknown) => this.refused(failure),
    });
  }

  protected recordSpend(): void {
    this.spend.markAllAsTouched();
    if (this.spend.invalid || this.saving()) {
      return;
    }

    const { kind, amountBaht, paidBy, note } = this.spend.getRawValue();

    this.saving.set(true);
    this.saveError.set(null);

    this.shop
      .spend(this.venueId(), {
        kind,
        amountBaht,
        paidOn: plainDate(venueToday()),
        paidBy,
        note: note.trim() === '' ? null : note.trim(),
        itemId: null,
        quantity: null,
      })
      .subscribe({
        next: (recorded) => {
          this.saving.set(false);
          this.spending.update((all) => [recorded, ...all]);
          this.spend.patchValue({ amountBaht: 0, note: '' });
        },
        error: (failure: unknown) => this.refused(failure),
      });
  }

  protected voidSpend(one: Spend): void {
    if (this.saving()) {
      return;
    }

    this.saving.set(true);
    this.saveError.set(null);

    this.shop.voidSpend(this.venueId(), one.spendId, this.i18n.t('shop.keyedWrong')).subscribe({
      next: (voided) => {
        this.saving.set(false);
        this.spending.update((all) =>
          all.map((row) => (row.spendId === voided.spendId ? voided : row)),
        );
      },
      error: (failure: unknown) => this.refused(failure),
    });
  }

  protected addItem(): void {
    this.item.markAllAsTouched();
    if (this.item.invalid || this.saving()) {
      return;
    }

    const asked = this.item.getRawValue();

    this.saving.set(true);
    this.saveError.set(null);

    this.shop
      .addItem(this.venueId(), {
        name: asked.name.trim(),
        priceBaht: asked.priceBaht,
        unit: asked.unit.trim(),
        counted: asked.counted,
        tellMeAt: asked.counted ? asked.tellMeAt : null,
      })
      .subscribe({
        next: (added) => {
          this.saving.set(false);
          this.items.update((all) => [...all, added]);
          this.item.patchValue({ name: '', unit: '' });
          this.item.controls.name.markAsUntouched();
          this.item.controls.unit.markAsUntouched();
        },
        error: (failure: unknown) => this.refused(failure),
      });
  }

  protected withdrawItem(one: ShopItem): void {
    if (this.saving()) {
      return;
    }

    this.saving.set(true);
    this.saveError.set(null);

    this.shop.withdrawItem(this.venueId(), one.itemId).subscribe({
      next: (gone) => {
        this.saving.set(false);
        this.items.update((all) => all.map((row) => (row.itemId === gone.itemId ? gone : row)));
      },
      error: (failure: unknown) => this.refused(failure),
    });
  }

  private refused(failure: unknown): void {
    this.saving.set(false);
    this.saveError.set(errorKey(failure));
  }

  private reloadItems(): void {
    this.shop.items(this.venueId()).subscribe({
      next: (items) => this.items.set(items),
      error: (failure: unknown) => this.pageError.set(errorKey(failure)),
    });
  }

  private reloadSpending(): void {
    this.shop.spending(this.venueId()).subscribe({
      next: (spending) => this.spending.set(spending),
      error: () => undefined,
    });
  }

  private load(venueId: string): void {
    this.loading.set(true);
    this.pageError.set(null);

    forkJoin({
      items: this.shop.items(venueId),
      sales: this.shop.sales(venueId, plainDate(venueToday())),
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

function itemId(item: ShopItem): string {
  return item.itemId;
}
