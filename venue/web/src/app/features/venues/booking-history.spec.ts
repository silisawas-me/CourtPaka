import { BookingHistoryEntry } from '../../core/venues/venue-bookings.service';
import { TRANSLATIONS } from '../../testing/translations';
import { historyLine } from './booking-history';

describe('historyLine', () => {
  const t = (key: string) => TRANSLATIONS.th[key] ?? key;

  function entry(overrides: Partial<BookingHistoryEntry>): BookingHistoryEntry {
    return {
      at: '2026-09-30T10:00:00Z',
      kind: 'Status',
      from: null,
      to: null,
      amountBaht: null,
      method: null,
      fromCourt: null,
      toCourt: null,
      hours: 0,
      cause: null,
      by: null,
      ...overrides,
    };
  }

  it('says the booking was made, then each step it took', () => {
    expect(historyLine(entry({ to: 'Confirmed' }), t, 'th-TH')).toBe(
      TRANSLATIONS.th['history.created'],
    );
    expect(historyLine(entry({ from: 'Confirmed', to: 'Cancelled' }), t, 'th-TH')).toBe(
      TRANSLATIONS.th['history.status.Cancelled'],
    );
    // The same status twice is money settled, not a move.
    expect(historyLine(entry({ from: 'Cancelled', to: 'Cancelled' }), t, 'th-TH')).toBe(
      TRANSLATIONS.th['history.settled'],
    );
  });

  it('says where the hours went and how much money moved', () => {
    expect(
      historyLine(
        entry({ kind: 'Hours', to: 'Moved', fromCourt: 'คอร์ต 2', toCourt: 'คอร์ต 3', hours: 2 }),
        t,
        'th-TH',
      ),
    ).toBe('ย้ายจากคอร์ต 2ไปคอร์ต 3');
    expect(
      historyLine(entry({ kind: 'Payment', amountBaht: 1200, method: 'Cash' }), t, 'th-TH'),
    ).toBe(
      `${TRANSLATIONS.th['history.payment']} ฿1,200 · ${TRANSLATIONS.th['money.method.Cash']}`,
    );
  });
});
