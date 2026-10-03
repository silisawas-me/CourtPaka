import { shiftPlainDate, venueClock, venueNow } from '../../core/i18n/plain-date';
import { QueuedSlip } from '../../core/venues/slips.service';

/** What the desk calls the booker: the name they gave, else their address's first part, else the phone. */
export function slipName(slip: QueuedSlip): string {
  return slip.bookerName ?? slip.bookerEmail?.split('@')[0] ?? slip.bookerPhone ?? '—';
}

/** "19:00–21:00", on a Bangkok wall. */
export function slipHours(slip: QueuedSlip): string {
  return `${venueClock(slip.startsAt)}–${venueClock(slip.endsAt)}`;
}

/** Which day it plays, against today: `today`, `tomorrow`, or the plain date for the date pipe. */
export function slipDay(slip: QueuedSlip, now: Date = new Date()): 'today' | 'tomorrow' | string {
  const day = venueNow(new Date(slip.startsAt)).date;
  const today = venueNow(now).date;
  if (day === today) {
    return 'today';
  }
  return day === shiftPlainDate(today, 1) ? 'tomorrow' : day;
}

/**
 * What the venue typed as the amount on the slip, read as money: a positive number with at most
 * two decimals, commas allowed. Null for anything else — the page says so rather than guessing.
 */
export function slipAmount(typed: string): number | null {
  const clean = typed.replace(/,/g, '').trim();
  if (!/^\d+(\.\d{1,2})?$/.test(clean)) {
    return null;
  }
  const value = Number(clean);
  return value > 0 ? value : null;
}
