import {
  DailyClosing,
  DayMoney,
  MoneyLead,
  MoneyLine,
  PaymentMethod,
} from '../../core/venues/venue-bookings.service';

/**
 * The drawer page's arithmetic, apart from the page (thai-fit T2, the "ปิดยอด" artboard): the
 * day cut into its shifts, and what each shift took — every number here is a sum of the lines
 * the server sent, so the tiles, the list and the count can never disagree with one another.
 */

/** Morning, afternoon or evening, said from the hour a shift began. */
export type ShiftPart = 'morning' | 'afternoon' | 'evening';

export interface Shift {
  /** Where the shift began: the count before it, or the start of the venue's day. */
  from: string;
  /** Where it ended; null for the shift still open. */
  until: string | null;
  /** The count that ended it; null while it is open. */
  count: DailyClosing | null;
  /** The hour shown as its start: opening time for the first shift of the day. */
  startsHour: number;
  part: ShiftPart;
}

export function partOf(hour: number): ShiftPart {
  return hour < 11 ? 'morning' : hour < 16 ? 'afternoon' : 'evening';
}

/** The hour on a Bangkok wall an instant falls in. */
export function bangkokHour(at: string): number {
  return Number(
    new Intl.DateTimeFormat('en-GB', {
      timeZone: 'Asia/Bangkok',
      hour: '2-digit',
      hourCycle: 'h23',
    }).format(new Date(at)),
  );
}

/** The day's shifts in order: each count closes one, and the open shift follows the last. */
export function shiftsOf(money: DayMoney): Shift[] {
  const counts = money.counts ?? [];
  const opens = money.opensHour ?? null;
  const shifts: Shift[] = counts.map((count, index) => {
    const from = count.from ?? count.closedAt;
    const startsHour = index === 0 && opens !== null ? opens : bangkokHour(from);
    return { from, until: count.closedAt, count, startsHour, part: partOf(startsHour) };
  });
  if (money.openShift) {
    const from = money.openShift.from;
    const startsHour = counts.length === 0 && opens !== null ? opens : bangkokHour(from);
    shifts.push({ from, until: null, count: null, startsHour, part: partOf(startsHour) });
  }
  return shifts;
}

/** Whether a line belongs to a shift: after it began, and no later than its count. */
export function inShift(line: MoneyLine, shift: Shift): boolean {
  const at = Date.parse(line.at);
  return at > Date.parse(shift.from) && (shift.until === null || at <= Date.parse(shift.until));
}

export interface ShiftTotals {
  all: number;
  court: number;
  shop: number;
  package: number;
  byMethod: Record<PaymentMethod, number>;
  cashIn: number;
  cashOut: number;
}

/** What came in, by what for and by how, and the cash that went in and out of the drawer. */
export function totalsOf(lines: readonly MoneyLine[]): ShiftTotals {
  const totals: ShiftTotals = {
    all: 0,
    court: 0,
    shop: 0,
    package: 0,
    byMethod: { Cash: 0, PromptPay: 0, Card: 0, BankTransfer: 0, TrueMoney: 0 },
    cashIn: 0,
    cashOut: 0,
  };
  for (const line of lines) {
    if (line.out) {
      if (line.method === 'Cash') {
        totals.cashOut += line.amountBaht;
      }
      continue;
    }
    totals.all += line.amountBaht;
    totals.byMethod[line.method] = (totals.byMethod[line.method] ?? 0) + line.amountBaht;
    if (line.kind === 'Sale') {
      totals.shop += line.amountBaht;
    } else if (line.kind === 'Package') {
      totals.package += line.amountBaht;
    } else {
      totals.court += line.amountBaht;
    }
    if (line.method === 'Cash') {
      totals.cashIn += line.amountBaht;
    }
  }
  return totals;
}

/** The artboard groups what a difference may be into three: cash in, owing, cash out. */
export type LeadGroup = 'CashTaken' | 'StillOwed' | 'CashOut';

export function groupOf(lead: MoneyLead): LeadGroup {
  return lead.kind === 'CashTaken'
    ? 'CashTaken'
    : lead.kind === 'StillOwed'
      ? 'StillOwed'
      : 'CashOut';
}

/** The line a lead points at, so its card can say what it was rather than only how much. */
export function lineOf(lead: MoneyLead, lines: readonly MoneyLine[]): MoneyLine | null {
  if (lead.kind === 'StillOwed') {
    return lines.find((line) => line.bookingId && line.bookingId === lead.bookingId) ?? null;
  }
  return (
    lines.find(
      (line) =>
        line.amountBaht === lead.amountBaht &&
        line.method === 'Cash' &&
        lead.at !== null &&
        Date.parse(line.at) === Date.parse(lead.at),
    ) ?? null
  );
}
