import { formatBaht } from '../../core/i18n/baht.pipe';
import { BookingHistoryEntry } from '../../core/venues/venue-bookings.service';

/**
 * One line of a booking's story in the reader's words. The server sends names and values only
 * (US-23); which words they become is decided here, once, for the panel.
 */
export function historyLine(
  entry: BookingHistoryEntry,
  t: (key: string) => string,
  locale: string,
): string {
  const baht = `฿${formatBaht(entry.amountBaht ?? 0, locale)}`;
  switch (entry.kind) {
    case 'Status':
      if (entry.from === null) {
        return t('history.created');
      }
      if (entry.from === entry.to) {
        return t('history.settled');
      }
      return t(`history.status.${entry.to}`);
    case 'Arrival':
      return t(`history.arrival.${entry.to}`);
    case 'Hours':
      return t(`history.hours.${entry.to}`)
        .replace('{n}', String(entry.hours))
        .replace('{from}', entry.fromCourt ?? '')
        .replace('{to}', entry.toCourt ?? '');
    case 'Payment':
      return `${t('history.payment')} ${baht} · ${t(`money.method.${entry.method}`)}`;
    case 'Refund':
      return `${t('history.refund')} ${baht} · ${t(`refunds.method.${entry.method}`)}`;
    case 'RefundVoided':
      return `${t('history.refund')} ${baht} · ${t('history.voided')}`;
  }
}
