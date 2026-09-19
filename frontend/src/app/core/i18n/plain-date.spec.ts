import { AppDatePipe } from './app-date.pipe';
import { fromPlainDate, plainDate, venueToday } from './plain-date';

describe('plain dates', () => {
  it('reads a plain date as that day, not as UTC midnight', () => {
    const date = fromPlainDate('2026-10-01')!;

    // new Date('2026-10-01') is midnight UTC, which is 30 September for anyone west of Greenwich.
    expect(date.getFullYear()).toBe(2026);
    expect(date.getMonth()).toBe(9);
    expect(date.getDate()).toBe(1);
  });

  it('writes back the date it was given', () => {
    expect(plainDate(fromPlainDate('2026-10-01')!)).toBe('2026-10-01');
  });

  it('refuses anything that is not a plain date', () => {
    expect(fromPlainDate('19 ก.ย. 2569')).toBeNull();
    expect(fromPlainDate('2026-13-01')).toBeNull();
    expect(fromPlainDate('')).toBeNull();
  });

  it('answers with the Bangkok date, whatever the browser is set to', () => {
    const bangkok = new Intl.DateTimeFormat('en-CA', {
      timeZone: 'Asia/Bangkok',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
    }).format(new Date());

    expect(plainDate(venueToday())).toBe(bangkok);
  });
});

describe('AppDatePipe', () => {
  const pipe = new AppDatePipe();

  it('writes a Thai date in the Buddhist era', () => {
    expect(pipe.transform('2026-10-01', 'th-TH')).toBe('1 ต.ค. 2569');
  });

  it('writes an English date in the common era', () => {
    expect(pipe.transform('2026-10-01', 'en-GB')).toBe('1 Oct 2026');
  });

  it('says nothing about a date it cannot read', () => {
    expect(pipe.transform('not a date', 'th-TH')).toBe('');
  });
});
