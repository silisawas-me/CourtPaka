import { ComponentFixture, TestBed } from '@angular/core/testing';
import { elementOf, pageProviders } from '../../testing/dom';
import { DayBoard } from './day-board';

/** One court, open six until eleven, nothing sold. */
function grid() {
  const hours = [6, 7, 8, 9, 10];
  return {
    venue: { id: 'v1', name: 'Smash Court', addressLine: '', district: '', province: '' },
    date: '2026-09-21',
    lastBookableDate: '2026-10-21',
    opensHour: hours[0],
    closesHour: hours[hours.length - 1] + 1,
    courts: [
      {
        courtId: 'c1',
        name: 'Court 1',
        hours: hours.map((hour) => ({ hour, status: 'Free', bahtPerHour: 200 })),
      },
    ],
  };
}

describe('DayBoard, the clock across the floor', () => {
  let fixture: ComponentFixture<DayBoard>;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [DayBoard], providers: pageProviders() });
    fixture = TestBed.createComponent(DayBoard);
    fixture.componentRef.setInput('day', grid());
    fixture.componentRef.setInput('bookings', []);
  });

  /** The same board, seen at the moment given — the clock is an input, so no rebuild. */
  function render(now: Date | null): void {
    fixture.componentRef.setInput('now', now);
    fixture.detectChanges();
  }

  function line(): HTMLElement | null {
    return elementOf<HTMLElement>(fixture, 'board-live');
  }

  /*
   * Read as a number, which is also what the stylesheet does with it: the rule multiplies a
   * length by this value, and bound with a unit it would be a percentage — multiplying a length
   * by a percentage is not arithmetic CSS does, so the whole calc falls back to `auto` and the
   * clock stands against the left edge of the floor at every hour of the day. Which is what
   * shipped, because nothing here had ever looked at the value.
   */
  it('stands at the share of the day that has gone', () => {
    // Half past eight: two and a half hours into the five the venue is open.
    render(new Date(2026, 8, 21, 8, 30));

    expect(Number(line()!.style.getPropertyValue('--at'))).toBeCloseTo(50, 5);
  });

  /*
   * Six o'clock exactly is nought hours into the day, and nought is a real position that reads
   * as falsy — `@if (liveAt(); as at)` dropped the clock for the first minute of every opening
   * hour, which is the minute somebody is unlocking the door and looking at the board.
   */
  it('is drawn on the stroke of the hour the venue opens', () => {
    render(new Date(2026, 8, 21, 6, 0));

    expect(line()).not.toBeNull();
    expect(Number(line()!.style.getPropertyValue('--at'))).toBeCloseTo(0, 5);
  });

  it('is not drawn before the venue opens or after it shuts', () => {
    render(new Date(2026, 8, 21, 5, 59));
    expect(line()).toBeNull();

    render(new Date(2026, 8, 21, 11, 1));
    expect(line()).toBeNull();
  });

  it('is not drawn at all on a day that is not today', () => {
    render(null);

    expect(line()).toBeNull();
  });
});
