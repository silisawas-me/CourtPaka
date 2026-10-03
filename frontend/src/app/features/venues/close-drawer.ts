import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { AuthService } from '../../core/auth/auth.service';
import { errorKey } from '../../core/http/api-error';
import { BahtPipe, formatBaht } from '../../core/i18n/baht.pipe';
import { fromPlainDate, plainDate, venueNow } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import {
  DailyClosing,
  DayMoney,
  MoneyLead,
  MoneyLine,
  PaymentMethod,
  VenueBookingsService,
} from '../../core/venues/venue-bookings.service';
import { groupOf, inShift, LeadGroup, lineOf, Shift, shiftsOf, totalsOf } from './till';

/** The forms money arrives in, as the artboard's tiles lay them out (thai-fit T3). */
const METHODS: readonly { method: PaymentMethod; note: string }[] = [
  { method: 'Cash', note: 'closing.inTill' },
  { method: 'PromptPay', note: 'closing.inBank' },
  { method: 'BankTransfer', note: 'closing.straightToBank' },
  { method: 'TrueMoney', note: 'closing.notInTill' },
  { method: 'Card', note: 'closing.notInTill' },
];

const LEAD_GROUPS: readonly LeadGroup[] = ['CashTaken', 'StillOwed', 'CashOut'];

/**
 * Counting the drawer by shift, as the "ปิดยอด" artboard of docs/plan/thai-fit.md draws it: the
 * day and its shifts across the top, what the shift took in six tiles, the cash in and out of the
 * drawer, the count, and — once a count comes out wrong — where the difference might be. What the
 * drawer should hold is the server's arithmetic; the page shows the same numbers first.
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
  private readonly auth = inject(AuthService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();

  /**
   * The venue's today. The calendar's until the server answers: at 01:00 a venue open until
   * 02:00 is still counting Friday's drawer (thai-fit T4), so the first read asks for no date
   * and the server says which day it is.
   */
  protected readonly today = signal(venueNow().date);
  protected readonly day = signal(this.today());
  private settled = false;
  protected readonly money = signal<DayMoney | null>(null);
  protected readonly pageError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly formError = signal<string | null>(null);

  protected readonly methods = METHODS;
  protected readonly leadGroups = LEAD_GROUPS;

  /** Which shift the page is on; the last one (the one still open) unless somebody picks. */
  protected readonly picked = signal<number | null>(null);
  /** The cash list shows the shift; "ดูรายการทั้งหมดของวัน" shows the whole day. */
  protected readonly wholeDay = signal(false);

  protected readonly floatTyped = signal<string | null>(null);
  protected readonly countedTyped = signal('');
  protected readonly note = signal('');

  protected readonly shifts = computed(() => {
    const money = this.money();
    return money ? shiftsOf(money) : [];
  });
  protected readonly at = computed(() => {
    const count = this.shifts().length;
    const picked = this.picked();
    return picked !== null && picked < count ? picked : count - 1;
  });
  protected readonly shift = computed<Shift | null>(() => this.shifts()[this.at()] ?? null);
  private readonly lines = computed(() => this.money()?.lines ?? []);
  protected readonly shiftLines = computed(() => {
    const shift = this.shift();
    return shift ? this.lines().filter((line) => inShift(line, shift)) : [];
  });
  protected readonly totals = computed(() => totalsOf(this.shiftLines()));
  protected readonly cashLines = computed(() =>
    (this.wholeDay() ? this.lines() : this.shiftLines()).filter((line) => line.method === 'Cash'),
  );
  protected readonly cashTotals = computed(() => totalsOf(this.cashLines()));

  protected readonly counts = computed(() => this.money()?.counts ?? []);
  protected readonly lastCount = computed(() => this.counts().at(-1) ?? null);

  /** Whoever is signed in is whoever is closing the shift that is open. */
  protected readonly me = computed(() => {
    const user = this.auth.currentUser();
    return user?.displayName || user?.email?.split('@')[0] || '';
  });

  /**
   * What the new shift starts with: the float the last shift started with. A shift that closes
   * hands in what it took ("ปิดกะ · ส่งเงิน") and leaves the float in the drawer, as the
   * artboard has it — somebody who handed over differently types what they were given.
   */
  private readonly handedFloat = computed(() => this.lastCount()?.openingFloatBaht ?? 0);
  protected readonly floatBaht = computed(() => {
    const typed = this.floatTyped();
    return typed === null ? this.handedFloat() : amountOf(typed);
  });
  protected readonly floatText = computed(() => this.floatTyped() ?? String(this.handedFloat()));

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

  /** Where the latest count came out, if it came out wrong: what the leads are about. */
  protected readonly outBy = computed(() => {
    const last = this.lastCount();
    return last && last.differenceBaht !== 0 ? Math.abs(last.differenceBaht) : null;
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
    effect(() => {
      const venueId = this.venueId();
      const day = this.day();
      untracked(() => {
        if (this.money()?.date !== day || !this.settled) {
          this.read(venueId, this.settled ? day : null);
        }
      });
    });
  }

  protected walk(by: number): void {
    const at = fromPlainDate(this.day()) ?? new Date();
    const next = plainDate(new Date(at.getFullYear(), at.getMonth(), at.getDate() + by));
    if (next <= this.today()) {
      this.picked.set(null);
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

  protected shiftName(shift: Shift | null): string {
    return shift ? this.i18n.t(`closing.part.${shift.part}`) : '';
  }

  protected shiftStart(shift: Shift): string {
    return shift === this.shifts()[0] && this.money()?.opensHour != null
      ? `${String(shift.startsHour).padStart(2, '0')}:00`
      : this.clock(shift.from);
  }

  /** Who a shift was: whoever counted it, or whoever is here for the one still open. */
  protected shiftWho(shift: Shift): string {
    return shift.count ? (shift.count.closedBy ?? '') : this.me();
  }

  /** The line's first line on the list: what the money was for. */
  protected what(line: MoneyLine): string {
    const t = (key: string) => this.i18n.t(key);
    if (line.out) {
      switch (line.kind) {
        case 'Refunded':
          return t('closing.line.refund');
        case 'SaleTakenBack':
          return `${t('closing.line.takenBack')}${line.note ? ` · ${line.note}` : ''}`;
        default:
          return line.note || t('closing.line.paidOut');
      }
    }
    switch (line.kind) {
      case 'Sale':
        return `${t('closing.kind.sale')} · ${(line.items ?? [])
          .map((item) => `${item.name} ×${item.quantity}`)
          .join(', ')}`;
      case 'Package':
        return `${t('closing.kind.package')} · ${line.packageHours} ${t('timeline.hr')}`;
      default: {
        const how = line.part
          ? t(`closing.line.${line.part}`)
          : line.bookingKind
            ? t(`board.kind.${line.bookingKind}`)
            : '';
        return how ? `${t('closing.kind.court')} · ${how}` : t('closing.kind.court');
      }
    }
  }

  /** The line's second line: whose it was, where, and who took it. */
  protected whose(line: MoneyLine): string {
    const t = (key: string) => this.i18n.t(key);
    const parts: string[] = [];
    if (line.kind === 'PaidOut') {
      if (line.spendKind) {
        parts.push(t(`closing.spend.${line.spendKind}`));
      }
      if (line.by) {
        parts.push(`${t('closing.recordedBy')} ${line.by}`);
      }
      return parts.join(' · ');
    }
    parts.push(line.who ?? (line.kind === 'Sale' ? t('closing.line.noBooking') : ''));
    if (line.courts) {
      parts.push(line.courts);
    }
    if (line.by && !line.out) {
      parts.push(`${t('closing.receivedBy')} ${line.by}`);
    }
    return parts.filter(Boolean).join(' · ');
  }

  protected leadsIn(group: LeadGroup): MoneyLead[] {
    return (this.money()?.leads ?? []).filter((lead) => groupOf(lead) === group);
  }

  /** What a lead was, from the line it points at: "น้ำดื่ม ×4 · ฿60", "คุณแนน · คอร์ต 5 · ค้าง ฿60". */
  protected leadTitle(lead: MoneyLead): string {
    const line = lineOf(lead, this.money()?.lines ?? []);
    const baht = `฿${formatBaht(lead.amountBaht, this.i18n.locale())}`;
    if (lead.kind === 'StillOwed') {
      const who = [line?.who, line?.courts].filter(Boolean).join(' · ');
      return `${who ? `${who} · ` : ''}${this.i18n.t('closing.owing')} ${baht}`;
    }
    return line ? `${this.whatShort(line)} · ${baht}` : baht;
  }

  protected leadHint(lead: MoneyLead): string {
    const at = lead.at ? `${this.clock(lead.at)} · ` : '';
    const line = lead.kind === 'CashTaken' ? lineOf(lead, this.money()?.lines ?? []) : null;
    const noBooking =
      line && line.kind === 'Sale' && !line.bookingId
        ? `${this.i18n.t('closing.line.noBooking')} · `
        : '';
    return `${at}${noBooking}${this.i18n.t(`closing.leadHint.${lead.kind}`)}`;
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
          this.picked.set(null);
          // The leads and the next shift come with the day, not with the answer to the count.
          this.read(this.venueId(), this.day());
        },
        error: (failure: unknown) => {
          this.busy.set(false);
          this.formError.set(errorKey(failure));
        },
      });
  }

  private whatShort(line: MoneyLine): string {
    return line.kind === 'Sale'
      ? (line.items ?? []).map((item) => `${item.name} ×${item.quantity}`).join(', ')
      : this.what(line);
  }

  private read(venueId: string, date: string | null): void {
    this.pageError.set(null);
    this.bookings.money(venueId, date).subscribe({
      next: (money) => {
        this.money.set(money);
        if (!this.settled) {
          this.settled = true;
          this.today.set(money.date);
          this.day.set(money.date);
        }
      },
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
