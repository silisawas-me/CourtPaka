import { BahtPipe, formatBaht } from './baht.pipe';
import { DATE_LOCALES } from './locales';

describe('BahtPipe', () => {
  it('groups thousands the way the reader expects', () => {
    expect(new BahtPipe().transform(1200, DATE_LOCALES.th)).toBe('1,200');
    expect(new BahtPipe().transform(1200, DATE_LOCALES.en)).toBe('1,200');
  });

  it('writes money to the satang and no further', () => {
    expect(new BahtPipe().transform(1200.5, DATE_LOCALES.th)).toBe('1,200.5');
    expect(new BahtPipe().transform(0.125, DATE_LOCALES.th)).toBe('0.13');
  });

  /** A figure the server has not answered for is nothing, not "NaN" across the table. */
  it('shows nothing rather than a word where there is no number', () => {
    expect(new BahtPipe().transform(null, DATE_LOCALES.th)).toBe('');
    expect(new BahtPipe().transform(undefined, DATE_LOCALES.th)).toBe('');
    expect(new BahtPipe().transform(Number.NaN, DATE_LOCALES.th)).toBe('');
  });

  it('is zero, which is a number, rather than nothing', () => {
    expect(new BahtPipe().transform(0, DATE_LOCALES.th)).toBe('0');
  });

  it('formats the same either way it is called', () => {
    expect(formatBaht(1200, DATE_LOCALES.th)).toBe(new BahtPipe().transform(1200, DATE_LOCALES.th));
  });
});
