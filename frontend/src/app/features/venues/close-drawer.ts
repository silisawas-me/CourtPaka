import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { errorKey } from '../../core/http/api-error';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { fromPlainDate, plainDate, venueNow } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import {
  DailyClosing,
  DayMoney,
  PaymentMethod,
  PaymentReceipt,
  VenueBookingsService,
} from '../../core/venues/venue-bookings.service';

/** The forms money arrives in, as the closing page lays them out (thai-fit T3). */
const METHODS: readonly { method: PaymentMethod; key: keyof DayMoney; inTill: boolean }[] = [
  { method: 'Cash', key: 'cashBaht', inTill: true },
  { method: 'PromptPay', key: 'promptPayBaht', inTill: false },
  { method: 'BankTransfer', key: 'bankTransferBaht', inTill: false },
  { method: 'TrueMoney', key: 'trueMoneyBaht', inTill: false },
  { method: 'Card', key: 'cardBaht', inTill: false },
];

/**
 * Counting the drawer, by shift (docs/plan/thai-fit.md T2, the "ปิดยอด" artboard): the day's
 * money by the form it came in, the cash that went in and out, and the count — a shift hands the
 * drawer over, the last count closes the day. What the drawer should hold is the server's
 * arithmetic; this page shows the same numbers so nobody is surprised by the answer.
 */
@Component({
  selector: 'app-close-drawer',
  imports: [BahtPipe],
  templateUrl: './close-drawer.html',
  styleUrl: './close-drawer.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CloseDrawer {
  private readonly bookings = inject(VenueBookingsService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();

  protected readonly today = venueNow().date;
  protected readonly day = signal(this.today);
  protected readonly money = signal<DayMoney | null>(null);
  protected readonly pageError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly formError = signal<string | null>(null);

  protected readonly methods = METHODS;

  /** What the shift was handed: what the last count left in the drawer, until somebody types. */
  protected readonly floatTyped = signal<string | null>(null);
  protected readonly countedTyped = signal('');
  protected readonly note = signal('');

  protected readonly counts = computed(() => this.money()?.counts ?? []);
  protected readonly closed = computed(() => this.money()?.closed ?? null);
  protected readonly lastCount = computed(() => this.counts().at(-1) ?? null);

  protected readonly floatBaht = computed(() => {
    const typed = this.floatTyped();
    return typed === null ? (this.lastCount()?.countedCashBaht ?? 0) : amountOf(typed);
  });
  protected readonly floatText = computed(
    () => this.floatTyped() ?? String(this.lastCount()?.countedCashBaht ?? 0),
  );

  /** Float + cash in − cash out of the shift now open: the server's sum, shown before it is asked. */
  protected readonly expected = computed(() => {
    const shift = this.money()?.openShift;
    return shift ? round(this.floatBaht() + shift.cashInBaht - shift.cashOutBaht) : 0;
  });

  protected readonly countedBaht = computed(() =>
    this.countedTyped().trim() === '' ? null : amountOf(this.countedTyped()),
  );

  protected readonly difference = computed(() => {
    const counted = this.countedBaht();
    return counted === null ? null : round(counted - this.expected());
  });

  protected readonly cashOutBaht = computed(() => {
    const money = this.money();
    return money ? money.cashRefundedBaht + money.cashPaidOutBaht : 0;
  });

  protected readonly dayLabel = computed(() =>
    new Intl.DateTimeFormat(this.i18n.locale(), {
      weekday: 'short',
      day: 'numeric',
      month: 'short',
      year: 'numeric',
    }).format(fromPlainDate(this.day()) ?? new Date()),
  );

  constructor() {
    effect(() => this.read(this.venueId(), this.day()));
  }

  protected walk(by: number): void {
    const at = fromPlainDate(this.day()) ?? new Date();
    const next = plainDate(new Date(at.getFullYear(), at.getMonth(), at.getDate() + by));
    if (next <= this.today) {
      this.day.set(next);
    }
  }

  protected clock(at: string | null): string {
    return at
      ? new Intl.DateTimeFormat('en-GB', {
          timeZone: 'Asia/Bangkok',
          hour: '2-digit',
          minute: '2-digit',
          hourCycle: 'h23',
        }).format(new Date(at))
      : '';
  }

  /** What a cash row was for: a court, a package sold, or something from the shop. */
  protected kindOf(receipt: PaymentReceipt): string {
    return receipt.saleId
      ? 'closing.kind.sale'
      : receipt.packageId
        ? 'closing.kind.package'
        : 'closing.kind.court';
  }

  /** Short or over is said in words beside it, so the amount is shown without a sign. */
  protected abs(baht: number): number {
    return Math.abs(baht);
  }

  protected standing(count: DailyClosing): 'even' | 'short' | 'over' {
    return count.differenceBaht === 0 ? 'even' : count.differenceBaht < 0 ? 'short' : 'over';
  }

  /** A shift hands over (`endsDay` false) or the day closes (`true`). */
  protected count(endsDay: boolean): void {
    const counted = this.countedBaht();
    if (counted === null || this.busy()) {
      return;
    }
    const note = this.note().trim();
    this.busy.set(true);
    this.formError.set(null);
    this.bookings
      .closeDay(this.venueId(), this.day(), this.floatBaht(), counted, note || undefined, endsDay)
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.floatTyped.set(null);
          this.countedTyped.set('');
          this.note.set('');
          // The leads and the next shift come with the day, not with the answer to the count.
          this.read(this.venueId(), this.day());
        },
        error: (failure: unknown) => {
          this.busy.set(false);
          this.formError.set(errorKey(failure));
        },
      });
  }

  private read(venueId: string, date: string): void {
    this.pageError.set(null);
    this.bookings.money(venueId, date).subscribe({
      next: (money) => this.money.set(money),
      error: (failure: unknown) => {
        this.money.set(null);
        this.pageError.set(errorKey(failure));
      },
    });
  }
}

function amountOf(typed: string): number {
  const amount = Number(typed.replace(/,/g, ''));
  return Number.isFinite(amount) ? Math.max(0, amount) : 0;
}

function round(baht: number): number {
  return Math.round(baht * 100) / 100;
}
