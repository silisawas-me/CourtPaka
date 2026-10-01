import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { clickOn, elementOf, pageProviders, textOf } from '../../testing/dom';
import { TRANSLATIONS } from '../../testing/translations';
import { BookingsPage } from './bookings.page';

describe('BookingsPage', () => {
  let fixture: ComponentFixture<BookingsPage>;
  let httpMock: HttpTestingController;

  const today = '2026-09-30';

  function booking(id: string, hours: number[], overrides: object = {}) {
    return {
      bookingId: id,
      bookerEmail: null,
      bookerPhone: null,
      channel: 'Staff',
      kind: 'WalkIn',
      customerName: `Name ${id}`,
      customerPhone: '0812345678',
      status: 'Confirmed',
      arrival: 'Unconfirmed',
      arrivedAt: null,
      graceEndsAt: '2026-09-30T12:15:00Z',
      paymentState: 'Received',
      totalBaht: 300 * hours.length,
      takenBaht: 300 * hours.length,
      toPayBaht: 0,
      refundDueBaht: 0,
      sentBackBaht: 0,
      outstandingBaht: 0,
      slots: hours.map((hour) => ({
        courtId: 'c1',
        courtName: 'Court 1',
        date: today,
        hour,
        bahtPerHour: 300,
      })),
      can: { checkIn: false, takeMoney: false, extend: false, moveCourt: false, cancelChoices: [] },
      ...overrides,
    };
  }

  const grid = {
    venue: { id: 'v1', name: 'Ari' },
    date: today,
    lastBookableDate: today,
    opensHour: 8,
    closesHour: 24,
    courts: [{ courtId: 'c1', name: 'Court 1', hours: [] }],
  };

  beforeEach(() => {
    localStorage.clear();
    // 18:45 in Bangkok.
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-09-30T11:45:00Z'));
    TestBed.configureTestingModule({ imports: [BookingsPage], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    fixture.destroy();
    httpMock.match((request) => request.url.endsWith('/history'));
    httpMock.verify();
    vi.useRealTimers();
  });

  function render(day: object[], date?: string): void {
    fixture = TestBed.createComponent(BookingsPage);
    fixture.componentRef.setInput('venueId', 'v1');
    if (date) {
      fixture.componentRef.setInput('date', date);
    }
    fixture.detectChanges();
    const asked = httpMock.expectOne((request) => request.url === '/api/venues/v1/bookings');
    expect(asked.request.params.get('date')).toBe(date ?? today);
    asked.flush(day);
    httpMock.expectOne((request) => request.url === '/api/venues/v1/availability').flush(grid);
    httpMock.match('/api/venues/v1/shop/items').forEach((one) => one.flush([]));
    fixture.detectChanges();
  }

  it('lists the day, says how each stands, and adds up what came in and what is owed', () => {
    render([
      booking('b1', [17, 18], { arrival: 'Arrived' }),
      booking('b2', [19], {
        paymentState: 'NotReceived',
        takenBaht: 100,
        toPayBaht: 200,
        can: { checkIn: true, takeMoney: true, extend: false, moveCourt: false, cancelChoices: [] },
      }),
      booking('b3', [20, 21], { status: 'Cancelled', takenBaht: 0 }),
    ]);

    expect(textOf(fixture, 'list-state-b1')).toBe(TRANSLATIONS.th['bookingList.state.playing']);
    expect(textOf(fixture, 'list-state-b2')).toBe(TRANSLATIONS.th['bookingList.state.waiting']);
    expect(textOf(fixture, 'list-owe-b2')).toContain('฿200');
    expect(textOf(fixture, 'list-state-b3')).toBe(TRANSLATIONS.th['bookingList.state.cancelled']);
    // A cancelled booking's hours were not sold.
    expect(textOf(fixture, 'sum-hours')).toBe('3');
    expect(textOf(fixture, 'sum-taken')).toBe('฿700');
    expect(textOf(fixture, 'sum-owing')).toBe('฿200');
  });

  it('narrows to what still needs doing, with a count on each filter', () => {
    render([
      booking('b1', [17]),
      booking('b2', [19], {
        can: { checkIn: true, takeMoney: true, extend: false, moveCourt: false, cancelChoices: [] },
      }),
      booking('b3', [20], { status: 'NoShow' }),
    ]);

    expect(textOf(fixture, 'list-count-unpaid')).toBe('1');
    expect(textOf(fixture, 'list-count-noShow')).toBe('1');

    clickOn(fixture, 'list-filter-unpaid');
    fixture.detectChanges();
    expect(elementOf(fixture, 'list-row-b2')).not.toBeNull();
    expect(elementOf(fixture, 'list-row-b1')).toBeNull();

    clickOn(fixture, 'list-filter-cancelled');
    fixture.detectChanges();
    expect(textOf(fixture, 'list-empty')).toBe(TRANSLATIONS.th['bookingList.nothingFiltered']);
  });

  it('opens the pressed booking in the same panel as the timeline', () => {
    render([booking('b1', [17]), booking('b2', [19])]);
    expect(elementOf(fixture, 'panel-empty')).not.toBeNull();

    clickOn(fixture, 'list-row-b2');
    fixture.detectChanges();

    expect(textOf(fixture, 'panel-name')).toBe('Name b2');
    expect(elementOf(fixture, 'list-row-b2')!.getAttribute('aria-pressed')).toBe('true');
  });

  it('reads the day in the URL, and moves a day through the URL', async () => {
    render([], '2026-10-03');
    expect(textOf(fixture, 'list-empty')).toBe(TRANSLATIONS.th['bookingList.nothing']);

    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    clickOn(fixture, 'day-later');
    expect(navigate).toHaveBeenCalledWith([], {
      queryParams: { date: '2026-10-04' },
      queryParamsHandling: 'merge',
    });

    clickOn(fixture, 'day-today');
    expect(navigate).toHaveBeenLastCalledWith([], {
      queryParams: { date: null },
      queryParamsHandling: 'merge',
    });
  });

  it('searches every day by name or phone once enough is typed', async () => {
    render([booking('b1', [17])]);
    // The pause before searching reads the clock, so it needs one that moves.
    vi.useRealTimers();

    const search = elementOf<HTMLInputElement>(fixture, 'list-search')!;
    search.value = 'ป';
    search.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await new Promise((done) => setTimeout(done, 300));
    httpMock.expectNone((request) => request.url.endsWith('/bookings/find'));

    search.value = 'ปาล์ม';
    search.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await new Promise((done) => setTimeout(done, 300));
    const asked = httpMock.expectOne((request) => request.url === '/api/venues/v1/bookings/find');
    expect(asked.request.params.get('q')).toBe('ปาล์ม');
    asked.flush([
      booking('far', [19], {
        customerName: 'คุณปาล์ม',
        slots: [
          { courtId: 'c1', courtName: 'Court 1', date: '2026-10-12', hour: 19, bahtPerHour: 300 },
        ],
      }),
    ]);
    fixture.detectChanges();

    expect(elementOf(fixture, 'list-row-far')).not.toBeNull();
    expect(elementOf(fixture, 'list-row-b1')).toBeNull();
    // A result can be on any day, so it says which.
    expect(textOf(fixture, 'list-row-far')).toContain('12');
  });
});
