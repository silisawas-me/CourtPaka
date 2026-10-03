import { clockHour } from '../../core/i18n/clock.pipe';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { concat, EMPTY, Observable, toArray } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { ShopItem, ShopService } from '../../core/venues/shop.service';
import {
  BookingHistoryEntry,
  BookingHours,
  CancellationReason,
  PaymentMethod,
  RefundMethod,
  Refunds,
  VenueBooking,
  VenueBookingsService,
} from '../../core/venues/venue-bookings.service';
import { historyLine } from './booking-history';
import { whoIs } from './now-board';
import { span } from './timeline';
import { ReceiptSheet } from './receipt-sheet';

/** The three ways the panel takes money, left to right as the design draws them. */
const PAY_WITH: readonly PaymentMethod[] = [
  'PromptPay',
  'Card',
  'Cash',
  'BankTransfer',
  'TrueMoney',
];

/** How a refund goes back, the transfer first because that is how most do (PRD US-18). */
const REFUND_WITH: readonly RefundMethod[] = ['Transfer', 'Cash'];

/** A court the booking could be moved to: the page knows the courts, the server which are free. */
export interface PanelCourt {
  readonly courtId: string;
  readonly name: string;
}

/**
 * The booking the desk is dealing with (owner app, artboards b1/b2): check in, one more or one
 * fewer hour, another court, money in whole or in part, shuttles and drinks onto the bill,
 * no-show, cancelling and writing down a refund — and, at the foot, what has happened to it.
 *
 * The timeline and the booking list open the same panel. Every door is the server's (`can`,
 * `GET …/hours`); the panel lays them out and hands back what a door changed, so the page that
 * holds the day replaces that one row.
 */
@Component({
  selector: 'app-booking-panel',
  imports: [BahtPipe, ReceiptSheet],
  templateUrl: './booking-panel.html',
  styleUrls: ['./booking-panel.scss', './booking-booker.scss', './booking-history.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { 'data-testid': 'booking-panel' },
})
export class BookingPanel {
  private readonly bookings = inject(VenueBookingsService);
  private readonly shop = inject(ShopService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  readonly booking = input<VenueBooking | null>(null);
  readonly courts = input<readonly PanelCourt[]>([]);
  /** What the panel says with nothing chosen: the timeline's day, or the list's. */
  readonly emptyKey = input('timeline.nothingToday');

  /** The receipt preview is open over the page (thai-fit T6). */
  protected readonly receiptOpen = signal(false);

  /** A door changed this booking: the row to put in the day's place. */
  readonly changed = output<VenueBooking>();
  /** Money moved without a door answering with the row: the day is read again. */
  readonly refreshed = output<void>();

  private readonly bookingId = computed(() => this.booking()?.bookingId ?? null);
  /** Bumped by every write the panel makes, so the story is read again after it. */
  private readonly wrote = signal(0);

  protected readonly items = signal<ShopItem[]>([]);
  protected readonly quantities = signal<Record<string, number>>({});
  protected readonly options = signal<BookingHours | null>(null);
  protected readonly history = signal<BookingHistoryEntry[] | null>(null);
  protected readonly busy = signal(false);
  protected readonly panelError = signal<string | null>(null);

  protected readonly payWith = PAY_WITH;

  /** The cancel page's second line, as artboard b2 writes it: who · court · time · price. */
  protected readonly cancelWhere = computed(() => {
    const booking = this.booking();
    if (!booking || booking.slots.length === 0) {
      return '';
    }
    const { from, to } = span(booking);
    return `${whoIs(booking)} · ${courtsOf(booking)} · ${clockHour(from)}–${clockHour(to)}`;
  });

  /** What cancelling does to the hours, and — when nothing came in — to the money (artboard b2). */
  protected readonly freedLine = computed(() => {
    const booking = this.booking();
    if (!booking || booking.slots.length === 0) {
      return '';
    }
    const { from, to } = span(booking);
    const freed = this.i18n
      .t('timeline.freedAt')
      .replace('{courts}', courtsOf(booking))
      .replace('{from}', clockHour(from))
      .replace('{to}', clockHour(to));
    return booking.takenBaht > 0 ? freed : `${this.i18n.t('timeline.nothingPaid')} · ${freed}`;
  });

  /** The line under a reason: what it means, and what share it gives back (artboard b2). */
  protected reasonNote(choice: {
    reason: string;
    refundPercent: number;
    underHours: number | null;
  }): string {
    const share =
      choice.refundPercent >= 100
        ? this.i18n.t('timeline.refundAll')
        : this.i18n.t('timeline.refundShare').replace('{p}', String(choice.refundPercent));
    switch (choice.reason) {
      case 'CustomerRequest':
        return choice.underHours !== null
          ? `${this.i18n.t('timeline.underHours').replace('{h}', String(choice.underHours))} · ${share}`
          : share;
      case 'VenueInitiated':
        return `${this.i18n.t('timeline.venueReasons')} · ${share}`;
      default:
        return this.i18n.t('timeline.neverPaid');
    }
  }

  protected readonly where = computed(() => {
    const booking = this.booking();
    if (!booking || booking.slots.length === 0) {
      return '';
    }
    const { from, to, hours } = span(booking);
    return `${courtsOf(booking)} · ${clockHour(from)}–${clockHour(to)} · ${hours} ${this.i18n.t('timeline.hr')}`;
  });

  /** Other courts, and whether the booking's hours are free on each (the server's answer). */
  protected readonly moves = computed(() => {
    const booking = this.booking();
    if (!booking) {
      return [];
    }
    const on = new Set(booking.slots.map((slot) => slot.courtId));
    const free = new Set((this.options()?.move?.courts ?? []).map((court) => court.courtId));
    return this.courts()
      .filter((court) => !on.has(court.courtId))
      .map((court) => ({
        courtId: court.courtId,
        name: court.name,
        free: free.has(court.courtId),
      }));
  });

  protected readonly canExtend = computed(() => {
    const extend = this.options()?.extend;
    return (
      !!extend?.sameCourtId && extend.courts.some((court) => court.courtId === extend.sameCourtId)
    );
  });

  protected readonly itemsBaht = computed(() =>
    this.items().reduce(
      (sum, item) => sum + item.priceBaht * (this.quantities()[item.itemId] ?? 0),
      0,
    ),
  );

  /** What the court still owes — only when the server keeps that door open (US-26). */
  protected readonly courtOwed = computed(() => {
    const booking = this.booking();
    return booking?.can.takeMoney ? booking.toPayBaht : 0;
  });

  /**
   * What the court is paid now: what somebody typed, or all of it until they do (artboard b1) —
   * never more than is owed, never less than nothing. The rest stays owed.
   */
  protected readonly taking = signal<string | null>(null);
  protected readonly takingNow = computed(() => {
    const typed = this.taking();
    if (typed === null) {
      return this.courtOwed();
    }
    const amount = Number(typed.replace(/,/g, ''));
    return Number.isFinite(amount) ? Math.max(0, Math.min(this.courtOwed(), amount)) : 0;
  });
  protected readonly takingText = computed(() => this.taking() ?? String(this.courtOwed()));

  protected readonly due = computed(() => this.takingNow() + this.itemsBaht());

  /** Cancelling: the panel turns into artboard b2. */
  protected readonly cancelling = signal(false);
  protected readonly reason = signal<CancellationReason | null>(null);
  protected readonly paymentReceived = signal<boolean | null>(null);
  protected readonly cancelNote = signal('');

  /** What the chosen answer gives back, as the server priced it. */
  protected readonly refundDue = computed(() => {
    const booking = this.booking();
    const reason = this.reason();
    return booking?.can.cancelChoices.find((one) => one.reason === reason)?.refundBaht ?? 0;
  });

  protected readonly cancelReady = computed(() => {
    const booking = this.booking();
    return booking
      ? booking.can.cancelChoices.length > 0
        ? this.reason() !== null
        : this.paymentReceived() !== null
      : false;
  });

  /** When the no-show door opens: the venue's grace after the first hour, on the Bangkok clock. */
  protected readonly noShowAfter = computed(() => {
    const at = this.booking()?.graceEndsAt;
    // Only while it is still ahead: past it, a shut door has another reason than the clock.
    if (!at || new Date(at).getTime() <= Date.now()) {
      return null;
    }
    return clockOf(at);
  });

  /**
   * Writing down money sent back (PRD US-18), in the panel: what is owed and sent comes from the
   * server with the reader's own ceiling, and it refuses anything over either.
   */
  protected readonly refundWith = REFUND_WITH;
  protected readonly refunding = signal(false);
  protected readonly refunds = signal<Refunds | null>(null);
  protected readonly refundAmount = signal<string | null>(null);
  protected readonly refundMethod = signal<RefundMethod>('Transfer');
  protected readonly refundNote = signal('');

  /** What goes down now: what somebody typed, or everything still owed until they do. */
  protected readonly refundNow = computed(() => {
    const left = this.refunds()?.outstandingBaht ?? 0;
    const typed = this.refundAmount();
    if (typed === null) {
      return left;
    }
    const amount = Number(typed.replace(/,/g, ''));
    return Number.isFinite(amount) ? Math.max(0, amount) : 0;
  });
  protected readonly refundText = computed(
    () => this.refundAmount() ?? String(this.refunds()?.outstandingBaht ?? ''),
  );

  /** What has happened to the booking, oldest first, each in a line the reader can say. */
  protected readonly story = computed(() =>
    (this.history() ?? []).map((entry) => ({
      at: momentOf(entry.at, this.i18n.locale()),
      text: historyLine(entry, (key) => this.i18n.t(key), this.i18n.locale()),
      by: entry.by,
    })),
  );

  protected readonly who = whoIs;

  constructor() {
    effect(() => {
      const venueId = this.venueId();
      this.shop.items(venueId).subscribe({
        next: (items) => this.items.set(items.filter((item) => !item.withdrawnAt)),
        error: () => this.items.set([]),
      });
    });
    // Another booking on the panel: start clean.
    effect(() => {
      this.bookingId();
      untracked(() => {
        this.quantities.set({});
        this.taking.set(null);
        this.cancelling.set(false);
        this.refunding.set(false);
        this.panelError.set(null);
      });
    });
    // Its doors that need asking (other courts, one more hour), each time the row is new.
    effect(() => {
      const booking = this.booking();
      untracked(() => {
        this.options.set(null);
        if (booking && (booking.can.extend || booking.can.moveCourt)) {
          this.bookings.hours(this.venueId(), booking.bookingId).subscribe({
            next: (options) => this.options.set(options),
            error: () => this.options.set(null),
          });
        }
      });
    });
    // Its story: when another booking is chosen, and after the panel itself wrote to it.
    effect(() => {
      const id = this.bookingId();
      this.wrote();
      untracked(() => {
        this.history.set(null);
        if (id) {
          this.bookings.history(this.venueId(), id).subscribe({
            next: (history) => this.history.set(history),
            error: () => this.history.set([]),
          });
        }
      });
    });
  }

  protected openRefund(): void {
    const booking = this.booking();
    if (!booking) {
      return;
    }
    this.refunds.set(null);
    this.refundAmount.set(null);
    this.refundMethod.set('Transfer');
    this.refundNote.set('');
    this.panelError.set(null);
    this.refunding.set(true);
    this.bookings.refunds(this.venueId(), booking.bookingId).subscribe({
      next: (refunds) => this.refunds.set(refunds),
      error: (failure: unknown) => this.panelError.set(errorKey(failure)),
    });
  }

  protected recordRefund(): void {
    const booking = this.booking();
    if (!booking || this.refundNow() <= 0 || this.busy()) {
      return;
    }
    const note = this.refundNote().trim();
    this.busy.set(true);
    this.panelError.set(null);
    this.bookings
      .recordRefund(this.venueId(), booking.bookingId, {
        amountBaht: this.refundNow(),
        refundedOn: plainDate(venueToday()),
        method: this.refundMethod(),
        note: note === '' ? undefined : note,
      })
      .subscribe({
        next: (refunds) => {
          this.refunds.set(refunds);
          this.busy.set(false);
          this.refunding.set(false);
          this.wrote.update((n) => n + 1);
          this.refreshed.emit();
        },
        error: (failure: unknown) => {
          this.panelError.set(errorKey(failure));
          this.busy.set(false);
        },
      });
  }

  protected openCancel(): void {
    this.reason.set(null);
    this.paymentReceived.set(null);
    this.cancelNote.set('');
    this.panelError.set(null);
    this.cancelling.set(true);
  }

  protected cancel(): void {
    const booking = this.booking();
    if (!booking || !this.cancelReady()) {
      return;
    }
    const note = this.cancelNote().trim();
    this.run(
      this.bookings.cancel(this.venueId(), booking.bookingId, {
        reason: this.reason() ?? undefined,
        paymentReceived: this.paymentReceived() ?? undefined,
        note: note === '' ? undefined : note,
      }),
      () => this.cancelling.set(false),
    );
  }

  protected noShow(): void {
    const booking = this.booking();
    if (booking?.can.noShow) {
      this.run(this.bookings.noShow(this.venueId(), booking.bookingId));
    }
  }

  protected more(item: ShopItem, by: number): void {
    this.quantities.update((all) => ({
      ...all,
      [item.itemId]: Math.max(0, (all[item.itemId] ?? 0) + by),
    }));
  }

  protected checkIn(): void {
    const booking = this.booking();
    if (booking?.can.checkIn) {
      this.run(this.bookings.checkIn(this.venueId(), booking.bookingId));
    }
  }

  protected shorten(): void {
    const booking = this.booking();
    if (booking && this.options()?.shorten) {
      this.run(this.bookings.shorten(this.venueId(), booking.bookingId));
    }
  }

  protected extend(): void {
    const booking = this.booking();
    if (booking && this.canExtend()) {
      this.run(this.bookings.extend(this.venueId(), booking.bookingId));
    }
  }

  protected move(courtId: string): void {
    const booking = this.booking();
    if (booking) {
      this.run(this.bookings.moveCourt(this.venueId(), booking.bookingId, courtId));
    }
  }

  /**
   * Takes what is due in one press: what the court still owes, through the counter's money door
   * (US-26), and the shuttles and drinks as a sale onto this booking (US-32).
   */
  protected pay(method: PaymentMethod): void {
    const booking = this.booking();
    if (!booking || this.due() <= 0) {
      return;
    }
    const lines = this.items()
      .map((item) => ({ itemId: item.itemId, quantity: this.quantities()[item.itemId] ?? 0 }))
      .filter((line) => line.quantity > 0);

    const steps: Observable<unknown>[] = [];
    if (this.takingNow() > 0) {
      steps.push(
        this.bookings.takePayment(this.venueId(), booking.bookingId, this.takingNow(), method),
      );
    }
    if (lines.length > 0) {
      steps.push(
        this.shop.sell(this.venueId(), { lines, paidBy: method, bookingId: booking.bookingId }),
      );
    }

    this.busy.set(true);
    this.panelError.set(null);
    (steps.length ? concat(...steps).pipe(toArray()) : EMPTY).subscribe({
      next: () => {
        this.quantities.set({});
        this.taking.set(null);
        this.busy.set(false);
        this.wrote.update((n) => n + 1);
        this.refreshed.emit();
      },
      error: (failure: unknown) => {
        this.panelError.set(errorKey(failure));
        this.busy.set(false);
        this.refreshed.emit();
      },
    });
  }

  private run(door: Observable<VenueBooking>, after?: () => void): void {
    this.busy.set(true);
    this.panelError.set(null);
    door.subscribe({
      next: (changed) => {
        after?.();
        this.busy.set(false);
        this.changed.emit(changed);
        this.wrote.update((n) => n + 1);
      },
      error: (failure: unknown) => {
        this.panelError.set(errorKey(failure));
        this.busy.set(false);
      },
    });
  }
}

function courtsOf(booking: VenueBooking): string {
  return [...new Set(booking.slots.map((slot) => slot.courtName))].join(', ');
}

function clockOf(at: string): string {
  return new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Asia/Bangkok',
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  }).format(new Date(at));
}

/** "28 ก.ย. 14:12" on the Bangkok clock, in the reader's language. */
function momentOf(at: string, locale: string): string {
  const when = new Date(at);
  const day = new Intl.DateTimeFormat(locale, {
    timeZone: 'Asia/Bangkok',
    day: 'numeric',
    month: 'short',
  }).format(when);
  return `${day} ${clockOf(at)}`;
}
