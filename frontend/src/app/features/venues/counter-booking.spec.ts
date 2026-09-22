import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../testing/translations';
import { clickOn, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
import { CounterBooking } from './counter-booking';

/** Two courts, 17:00–20:00; one hour already taken online. */
function grid(date = '2026-09-22') {
  const hours = (taken: number[]) =>
    [17, 18, 19].map((hour) => ({
      hour,
      status: taken.includes(hour) ? 'Booked' : 'Free',
      bahtPerHour: hour >= 18 ? 300 : 200,
    }));

  return {
    venue: { id: 'v1', name: 'Smash Court', addressLine: '', district: '', province: '' },
    date,
    lastBookableDate: '2026-10-21',
    opensHour: 17,
    closesHour: 20,
    courts: [
      { courtId: 'c1', name: 'Court 1', hours: hours([]) },
      { courtId: 'c2', name: 'Court 2', hours: hours([18]) },
    ],
  };
}

describe('CounterBooking', () => {
  let fixture: ComponentFixture<CounterBooking>;
  let httpMock: HttpTestingController;

  function render(date = '2026-09-22', answer: object = grid(date)): void {
    TestBed.configureTestingModule({
      imports: [CounterBooking],
      providers: pageProviders(),
    });

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(CounterBooking);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('date', date);
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === '/api/venues/v1/availability').flush(answer);
    fixture.detectChanges();
  }

  beforeEach(() => {
    vi.useFakeTimers();
    // The day before the grid, so every hour on it is still ahead.
    vi.setSystemTime(new Date('2026-09-21T10:00:00Z'));
  });

  afterEach(() => {
    vi.useRealTimers();
    httpMock.verify();
    TestBed.resetTestingModule();
  });

  it('offers the free hours and not the taken ones', () => {
    render();

    expect(elementOf(fixture, 'counter-cell-c1-18')).not.toBeNull();
    expect(elementOf(fixture, 'counter-cell-c2-18')).toBeNull();
  });

  it('adds up what is picked, from the prices the grid was drawn with', () => {
    render();

    clickOn(fixture, 'counter-cell-c1-17');
    clickOn(fixture, 'counter-cell-c1-18');

    expect(textOf(fixture, 'counter-total')).toContain('500');

    // Picking again puts it back.
    clickOn(fixture, 'counter-cell-c1-17');
    expect(textOf(fixture, 'counter-total')).toContain('300');
  });

  it('sends the picks with who the customer is and how they paid', () => {
    render();
    const taken = vi.fn();
    fixture.componentInstance.taken.subscribe(taken);

    clickOn(fixture, 'counter-cell-c1-18');
    setInput(fixture, '[data-testid="customer-name"]', ' คุณสมชาย ');
    setInput(fixture, '[data-testid="customer-phone"]', '081-234-5678');
    clickOn(fixture, 'paid-Transfer');
    clickOn(fixture, 'counter-take');

    const request = httpMock.expectOne('/api/venues/v1/bookings');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      slots: [{ courtId: 'c1', date: '2026-09-22', hour: 18 }],
      customerName: 'คุณสมชาย',
      customerPhone: '081-234-5678',
      paidBy: 'Transfer',
    });

    request.flush({ bookingId: 'b1', channel: 'Staff' });
    expect(taken).toHaveBeenCalled();
  });

  it('will not book for nobody', () => {
    render();

    clickOn(fixture, 'counter-cell-c1-18');
    clickOn(fixture, 'counter-take');

    httpMock.expectNone('/api/venues/v1/bookings');
    expect(elementOf(fixture, 'customer-name-error')).not.toBeNull();
  });

  it('will not book no hours', () => {
    render();

    setInput(fixture, '[data-testid="customer-name"]', 'คุณสมชาย');
    clickOn(fixture, 'counter-take');

    httpMock.expectNone('/api/venues/v1/bookings');
    expect(textOf(fixture, 'counter-error')).toBe(TRANSLATIONS.th['counter.pickAnHour']);
  });

  /**
   * The counter may sell an hour that has started — the person is standing there — but not one
   * that is over (PRD US-13). Judged by the venue's clock, not the browser's.
   */
  it('offers an hour that has started and not one that is over', () => {
    // 18:30 in Bangkok on the grid's own day.
    vi.setSystemTime(new Date('2026-09-22T11:30:00Z'));
    render('2026-09-22');

    expect(elementOf(fixture, 'counter-cell-c1-17')).toBeNull();
    expect(elementOf(fixture, 'counter-cell-c1-18')).not.toBeNull();
    expect(elementOf(fixture, 'counter-cell-c1-19')).not.toBeNull();
  });

  it('reads the grid again when the server refuses, because it has usually moved on', () => {
    render();

    clickOn(fixture, 'counter-cell-c1-18');
    setInput(fixture, '[data-testid="customer-name"]', 'คุณสมชาย');
    clickOn(fixture, 'counter-take');

    httpMock
      .expectOne('/api/venues/v1/bookings')
      .flush({ code: 'booking.slot_just_taken' }, { status: 409, statusText: 'Conflict' });

    const again = grid();
    again.courts[0].hours[1].status = 'Booked';
    httpMock.expectOne((request) => request.url === '/api/venues/v1/availability').flush(again);
    fixture.detectChanges();

    expect(textOf(fixture, 'counter-error')).toBe(TRANSLATIONS.th['error.booking.slot_just_taken']);
    // The hour somebody else took is gone from the grid, and from the picks.
    expect(elementOf(fixture, 'counter-cell-c1-18')).toBeNull();
    expect(textOf(fixture, 'counter-total')).toContain(': 0');
  });
});
