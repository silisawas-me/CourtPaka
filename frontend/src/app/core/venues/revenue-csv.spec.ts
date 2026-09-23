import { revenueCsv, revenueCsvBlob } from './revenue-csv';
import { DashboardMonth } from './venue-dashboard.service';

const COLUMNS = {
  month: 'เดือน',
  bookings: 'จำนวนการจอง',
  online: 'ออนไลน์',
  counter: 'เคาน์เตอร์',
  refundDue: 'ยอดต้องคืน',
  refunded: 'ยอดคืนแล้ว',
};

function month(overrides: Partial<DashboardMonth> = {}): DashboardMonth {
  return {
    year: 2026,
    month: 9,
    onlineBaht: 1200,
    staffBaht: 400.5,
    bookings: 7,
    refundDueBaht: 200,
    refundedBaht: 100,
    ...overrides,
  };
}

describe('revenueCsv', () => {
  it('writes the headings it is handed, in the order PRD 7.3 asks for', () => {
    const [heading] = revenueCsv([month()], COLUMNS).split('\r\n');

    expect(heading).toBe('เดือน,จำนวนการจอง,ออนไลน์,เคาน์เตอร์,ยอดต้องคืน,ยอดคืนแล้ว');
  });

  it('writes a month a spreadsheet can sort and a person can read', () => {
    const [, row] = revenueCsv([month({ month: 1 })], COLUMNS).split('\r\n');

    expect(row).toBe('2026-01,7,1200.00,400.50,200.00,100.00');
  });

  it('keeps a name with a comma in it in one column', () => {
    const csv = revenueCsv([month()], { ...COLUMNS, month: 'เดือน, ปี' });

    expect(csv.split('\r\n')[0]).toContain('"เดือน, ปี"');
  });

  it('doubles a quote rather than ending the field on it', () => {
    const csv = revenueCsv([month()], { ...COLUMNS, month: 'the "month"' });

    expect(csv.split('\r\n')[0]).toContain('"the ""month"""');
  });

  it('ends every line, so the last row is not half a file', () => {
    expect(revenueCsv([month()], COLUMNS).endsWith('\r\n')).toBe(true);
  });

  it('is a file a spreadsheet opens as Thai rather than as mojibake', async () => {
    const blob = revenueCsvBlob(revenueCsv([month()], COLUMNS));

    expect(blob.type).toContain('charset=utf-8');

    // Read as bytes, not as text: text() decodes and drops the mark, so it cannot see the one
    // thing this is about. Excel reads the file as UTF-8 only if these three bytes are there.
    const bytes = new Uint8Array(await blob.arrayBuffer());
    expect([...bytes.slice(0, 3)]).toEqual([0xef, 0xbb, 0xbf]);
  });
});
