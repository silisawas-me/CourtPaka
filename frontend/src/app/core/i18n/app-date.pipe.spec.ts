import { AppDatePipe, AppDateTimePipe } from './app-date.pipe';
import { DATE_LOCALES } from './locales';

describe('AppDatePipe', () => {
  it('reads a plain date and prints the Buddhist year in Thai', () => {
    expect(new AppDatePipe().transform('2026-09-21', DATE_LOCALES.th)).toContain('2569');
  });

  it('answers nothing for something that is not a plain date', () => {
    // A full timestamp is the shape that used to render as an empty cell.
    expect(new AppDatePipe().transform('2026-09-21T11:00:00Z', DATE_LOCALES.th)).toBe('');
  });
});

describe('AppDateTimePipe', () => {
  it('reads a timestamp and keeps the time of day', () => {
    const printed = new AppDateTimePipe().transform('2026-09-21T11:00:00Z', DATE_LOCALES.th);

    expect(printed).toContain('2569');
    expect(printed).toMatch(/\d{2}:\d{2}/);
  });

  it('answers nothing rather than "Invalid Date"', () => {
    expect(new AppDateTimePipe().transform('not a time', DATE_LOCALES.th)).toBe('');
  });
});
