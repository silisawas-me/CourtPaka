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
import { RouterLink } from '@angular/router';
import { concat, EMPTY, Observable, toArray } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { plainDate, venueNow, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { Availability, PublicVenueService } from '../../core/venues/public-venue.service';
import { ShopItem, ShopService } from '../../core/venues/shop.service';
import {
  BookingHours,
  CancellationReason,
  PaymentMethod,
  RefundMethod,
  Refunds,
  VenueBooking,
  VenueBookingsService,
} from '../../core/venues/venue-bookings.service';
import { WalkInEvents } from '../../core/venues/walk-in.events';
import { whoIs } from './now-board';
import { NOW_REFRESH_MS } from './now.page';
import {
  dayHours,
  firstToShow,
  span,
  TIMELINE_HOURS,
  TimelineBlock,
  timelineRows,
  windowStart,
} from './timeline';

/** The three ways the panel takes money, left to right as the design draws them. */
const PAY_WITH: readonly PaymentMethod[] = ['PromptPay', 'Card', 'Cash'];

/** How a refund goes back, the transfer first because that is how most do (PRD US-18). */
const REFUND_WITH: readonly RefundMethod[] = ['Transfer', 'Cash'];

/**
 * The court schedule of one branch, as the owner app draws it: a track per court across nine
 * hours, a block per booking placed by the hour, the minute now as a line — and beside it, always
 * open, the booking the desk is dealing with: check in, one more hour, another court, shuttles
 * and drinks onto the bill, and what is left to take.
 *
 * Every door is the server's (`can`, `GET …/hours`), the same answers the day's list reads; this
 * page only lays them out. Cancelling, no-show and writing down a refund open in the panel too;
 * the list with the rest (voiding a refund, settling, another day) is its own page under "อื่น ๆ".
 */
@Component({
  selector: 'app-timeline-page',
  imports: [BahtPipe, RouterLink],
  templateUrl: './timeline.page.html',
  styleUrls: ['./timeline.page.scss', './timeline-panel.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TimelinePage {
  private readonly venues = inject(PublicVenueService);
  private readonly bookings = inject(VenueBookingsService);
  private readonly shop = inject(ShopService);
  private readonly walkIns = inject(WalkInEvents);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();

  private readonly clock = signal(venueNow());
  private readonly grid = signal<Availability | null>(null);
  private readonly day = signal<VenueBooking[] | null>(null);

  /** Where the window starts once somebody walked it; until then it follows the clock. */
  private readonly walked = signal<number | null>(null);
  private readonly chosen = signal<string | null>(null);

  protected readonly pageError = signal<string | null>(null);
  protected readonly items = signal<ShopItem[]>([]);
  protected readonly quantities = signal<Record<string, number>>({});
  protected readonly options = signal<BookingHours | null>(null);
  protected readonly busy = signal(false);
  protected readonly panelError = signal<string | null>(null);

  protected readonly payWith = PAY_WITH;

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

  /** The cancel page's second line, as artboard b2 writes it: who · court · time · price. */
  protected readonly cancelWhere = computed(() => {
    const booking = this.selected();
    if (!booking || booking.slots.length === 0) {
      return '';
    }
    const { from, to } = span(booking);
    const courts = [...new Set(booking.slots.map((slot) => slot.courtName))].join(', ');
    return `${whoIs(booking)} · ${courts} · ${from}:00–${to}:00`;
  });

  /** What cancelling does to the hours, and — when nothing came in — to the money (artboard b2). */
  protected readonly freedLine = computed(() => {
    const booking = this.selected();
    if (!booking || booking.slots.length === 0) {
      return '';
    }
    const { from, to } = span(booking);
    const courts = [...new Set(booking.slots.map((slot) => slot.courtName))].join(', ');
    const freed = this.i18n
      .t('timeline.freedAt')
      .replace('{courts}', courts)
      .replace('{from}', `${from}:00`)
      .replace('{to}', `${to}:00`);
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
    const booking = this.selected();
    if (!booking || booking.slots.length === 0) {
      return '';
    }
    const { from, to, hours } = span(booking);
    const courts = [...new Set(booking.slots.map((slot) => slot.courtName))].join(', ');
    return `${courts} · ${from}:00–${to}:00 · ${hours} ${this.i18n.t('timeline.hr')}`;
  });

  /** Other courts, and whether the booking's hours are free on each (the server's answer). */
  protected readonly moves = computed(() => {
    const booking = this.selected();
    const grid = this.grid();
    if (!booking || !grid) {
      return [];
    }
    const on = new Set(booking.slots.map((slot) => slot.courtId));
    const free = new Set((this.options()?.move?.courts ?? []).map((court) => court.courtId));
    return grid.courts
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
    const booking = this.selected();
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
    const booking = this.selected();
    const reason = this.reason();
    return booking?.can.cancelChoices.find((one) => one.reason === reason)?.refundBaht ?? 0;
  });

  protected readonly cancelReady = computed(() => {
    const booking = this.selected();
    return booking
      ? booking.can.cancelChoices.length > 0
        ? this.reason() !== null
        : this.paymentReceived() !== null
      : false;
  });

  /** When the no-show door opens: the venue's grace after the first hour, on the Bangkok clock. */
  protected readonly noShowAfter = computed(() => {
    const at = this.selected()?.graceEndsAt;
    // Only while it is still ahead: past it, a shut door has another reason than the clock.
    if (!at || new Date(at).getTime() <= Date.now()) {
      return null;
    }
    return new Intl.DateTimeFormat('en-GB', {
      timeZone: 'Asia/Bangkok',
      hour: '2-digit',
      minute: '2-digit',
      hourCycle: 'h23',
    }).format(new Date(at));
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

  protected readonly who = whoIs;

  constructor() {
    effect(() => this.read(this.venueId(), { first: true }));
    effect(() => {
      const venueId = this.venueId();
      this.shop.items(venueId).subscribe({
        next: (items) => this.items.set(items.filter((item) => !item.withdrawnAt)),
        error: () => this.items.set([]),
      });
    });
    // The doors of the booking on the panel: which courts it could move to, one more hour.
    effect(() => {
      const booking = this.selected();
      this.options.set(null);
      if (booking && (booking.can.extend || booking.can.moveCourt)) {
        this.bookings.hours(this.venueId(), booking.bookingId).subscribe({
          next: (options) => this.options.set(options),
          error: () => this.options.set(null),
        });
      }
    });

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
      this.quantities.set({});
      this.taking.set(null);
      this.cancelling.set(false);
      this.refunding.set(false);
      this.panelError.set(null);
    }
  }

  protected openRefund(): void {
    const booking = this.selected();
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
    const booking = this.selected();
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
          this.read(this.venueId(), { first: false });
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
    const booking = this.selected();
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
    const booking = this.selected();
    if (booking?.can.noShow) {
      this.run(this.bookings.noShow(this.venueId(), booking.bookingId));
    }
  }

  protected walk(by: number): void {
    const all = this.allHours();
    const last = all[all.length - 1] + 1 - TIMELINE_HOURS;
    this.walked.set(Math.max(all[0], Math.min(last, this.start() + by)));
  }

  protected more(item: ShopItem, by: number): void {
    this.quantities.update((all) => ({
      ...all,
      [item.itemId]: Math.max(0, (all[item.itemId] ?? 0) + by),
    }));
  }

  protected checkIn(): void {
    const booking = this.selected();
    if (booking?.can.checkIn) {
      this.run(this.bookings.checkIn(this.venueId(), booking.bookingId));
    }
  }

  /** A tap on an empty hour: the walk-in opens on that court and hour (the frame holds it). */
  protected bookAt(courtId: string, hour: number): void {
    this.walkIns.open.next({ venueId: this.venueId(), courtId, hour });
  }

  protected shorten(): void {
    const booking = this.selected();
    if (booking && this.options()?.shorten) {
      this.run(this.bookings.shorten(this.venueId(), booking.bookingId));
    }
  }

  protected extend(): void {
    const booking = this.selected();
    if (booking && this.canExtend()) {
      this.run(this.bookings.extend(this.venueId(), booking.bookingId));
    }
  }

  protected move(courtId: string): void {
    const booking = this.selected();
    if (booking) {
      this.run(this.bookings.moveCourt(this.venueId(), booking.bookingId, courtId));
    }
  }

  /**
   * Takes what is due in one press: what the court still owes, through the counter's money door
   * (US-26), and the shuttles and drinks as a sale onto this booking (US-32).
   */
  protected pay(method: PaymentMethod): void {
    const booking = this.selected();
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
        this.read(this.venueId(), { first: false });
      },
      error: (failure: unknown) => {
        this.panelError.set(errorKey(failure));
        this.busy.set(false);
        this.read(this.venueId(), { first: false });
      },
    });
  }

  private run(door: Observable<VenueBooking>, after?: () => void): void {
    this.busy.set(true);
    this.panelError.set(null);
    door.subscribe({
      next: (changed) => {
        after?.();
        // Stay on it: a booking just cancelled is no longer the one the panel would open on
        // its own, and what it still owes back is the next thing to do (PRD US-18).
        this.chosen.set(changed.bookingId);
        this.day.update((day) =>
          (day ?? []).map((one) => (one.bookingId === changed.bookingId ? changed : one)),
        );
        this.busy.set(false);
        // The grid of free courts changed too: a moved or longer booking holds other hours.
        this.read(this.venueId(), { first: true });
      },
      error: (failure: unknown) => {
        this.panelError.set(errorKey(failure));
        this.busy.set(false);
      },
    });
  }

  private read(venueId: string, { first }: { first: boolean }): void {
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
