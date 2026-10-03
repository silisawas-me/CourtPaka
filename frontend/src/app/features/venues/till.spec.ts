import { DayMoney, MoneyLine } from '../../core/venues/venue-bookings.service';
import { inShift, partOf, shiftsOf, totalsOf } from './till';

function line(fields: Partial<MoneyLine>): MoneyLine {
  return {
    at: '2026-10-03T03:00:00Z',
    out: false,
    kind: 'Court',
    method: 'Cash',
    amountBaht: 0,
    who: null,
    courts: null,
    bookingKind: null,
    part: null,
    items: null,
    packageHours: null,
    spendKind: null,
    note: null,
    by: null,
    ...fields,
  };
}

describe('till (the drawer page arithmetic)', () => {
  it('names a shift by the hour it began', () => {
    expect(partOf(8)).toBe('morning');
    expect(partOf(13)).toBe('afternoon');
    expect(partOf(16)).toBe('evening');
  });

  it('cuts the day at each count, and starts the first shift at opening time', () => {
    const shifts = shiftsOf({
      opensHour: 8,
      counts: [
        {
          from: '2026-10-02T17:00:00Z',
          closedAt: '2026-10-03T09:05:00Z',
          closedBy: 'ปุ้ย',
          endsDay: false,
        },
      ],
      openShift: { from: '2026-10-03T09:05:00Z', cashInBaht: 0, cashOutBaht: 0 },
    } as unknown as DayMoney);

    expect(shifts.map((one) => [one.part, one.startsHour, one.until])).toEqual([
      ['morning', 8, '2026-10-03T09:05:00Z'],
      ['evening', 16, null],
    ]);
    expect(inShift(line({ at: '2026-10-03T09:05:00Z' }), shifts[0])).toBe(true);
    expect(inShift(line({ at: '2026-10-03T09:06:00Z' }), shifts[1])).toBe(true);
  });

  it('adds a shift up by what it was for and how it came, and the drawer apart', () => {
    const totals = totalsOf([
      line({ kind: 'Court', method: 'Cash', amountBaht: 260 }),
      line({ kind: 'Sale', method: 'Card', amountBaht: 520 }),
      line({ kind: 'Package', method: 'Cash', amountBaht: 1_400 }),
      line({ out: true, kind: 'PaidOut', method: 'Cash', amountBaht: 450 }),
    ]);

    expect([totals.all, totals.court, totals.shop, totals.package]).toEqual([
      2_180, 260, 520, 1_400,
    ]);
    expect([totals.cashIn, totals.cashOut, totals.byMethod.Card]).toEqual([1_660, 450, 520]);
  });
});
