import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { clickOn, elementOf, pageProviders, textOf } from '../../testing/dom';
import { TimelinePage } from './timeline.page';

describe('TimelinePage', () => {
  let fixture: ComponentFixture<TimelinePage>;
  let httpMock: HttpTestingController;

  const date = '2026-09-30';

  function booking(id: string, court: string, hours: number[], overrides: object = {}) {
    return {
      bookingId: id,
      bookerEmail: null,
      bookerPhone: null,
      channel: 'Staff',
      kind: 'WalkIn',
      customerName: `Name ${id}`,
      customerPhone: null,
      status: 'Confirmed',
      arrival: 'Unconfirmed',
      arrivedAt: null,
      graceEndsAt: '2026-09-30T12:15:00Z',
      paymentState: 'NotReceived',
      totalBaht: 300 * hours.length,
      takenBaht: 0,
      toPayBaht: 300 * hours.length,
      refundDueBaht: 0,
      sentBackBaht: 0,
      outstandingBaht: 0,
      slots: hours.map((hour) => ({
        courtId: court,
        courtName: court,
        date,
        hour,
        bahtPerHour: 300,
      })),
      can: { checkIn: false, takeMoney: true, extend: false, moveCourt: false, cancelChoices: [] },
      ...overrides,
    };
  }

  const water = {
    itemId: 'i1',
    name: 'Water',
    priceBaht: 15,
    unit: 'bottle',
    counted: false,
    tellMeAt: null,
    left: null,
    runningLow: false,
    withdrawnAt: null,
  };

  beforeEach(() => {
    // 18:45 in Bangkok, as the design draws it.
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-09-30T11:45:00Z'));
    TestBed.configureTestingModule({ imports: [TimelinePage], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    fixture.destroy();
    httpMock.verify();
    vi.useRealTimers();
  });

  function render(day: object[]): void {
    fixture = TestBed.createComponent(TimelinePage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    const hours = Array.from({ length: 16 }, (_, index) => ({
      hour: 8 + index,
      status: 'Free',
      bahtPerHour: 8 + index >= 17 ? 320 : 220,
    }));
    httpMock
      .expectOne((request) => request.url === '/api/venues/v1/availability')
      .flush({
        venue: { id: 'v1', name: 'Ari' },
        date,
        lastBookableDate: date,
        opensHour: 8,
        closesHour: 24,
        courts: [
          { courtId: 'c1', name: 'Court 1', hours },
          { courtId: 'c2', name: 'Court 2', hours },
        ],
      });
    httpMock.expectOne((request) => request.url === '/api/venues/v1/bookings').flush(day);
    httpMock.expectOne('/api/venues/v1/shop/items').flush([water]);
    fixture.detectChanges();
  }

  it('draws nine hours from four before now, with the peak hours marked', () => {
    render([]);

    const heads = [...fixture.nativeElement.querySelectorAll('.hour')].map((one: Element) =>
      one.textContent?.trim(),
    );
    expect(heads[0]).toBe('14:00');
    expect(heads).toHaveLength(9);
    // 17:00 to 22:00 cost more than the cheapest hour of the day.
    expect(fixture.nativeElement.querySelectorAll('.peak').length).toBe(6);
    expect(elementOf(fixture, 'panel-empty')).not.toBeNull();
  });

  it('opens the booking the desk is waiting for, and another when its block is pressed', () => {
    render([
      booking('b1', 'c1', [18, 19]),
      booking('b2', 'c2', [19, 20], {
        can: { checkIn: true, takeMoney: true, extend: false, moveCourt: false, cancelChoices: [] },
      }),
    ]);

    expect(textOf(fixture, 'panel-name')).toBe('Name b2');
    expect(elementOf(fixture, 'due-b2')).not.toBeNull();

    clickOn(fixture, 'board-block-b1');
    fixture.detectChanges();
    expect(textOf(fixture, 'panel-name')).toBe('Name b1');
    expect(textOf(fixture, 'panel-where')).toContain('18:00–20:00');
  });

  it('takes the court and the drinks in one press', () => {
    render([booking('b1', 'c1', [19])]);

    clickOn(fixture, 'more-i1');
    clickOn(fixture, 'more-i1');
    fixture.detectChanges();
    // 300 for the court still owed, 2 × 15 for the water.
    expect(textOf(fixture, 'due')).toBe('฿330');

    clickOn(fixture, 'pay-Cash');
    const court = httpMock.expectOne('/api/venues/v1/bookings/b1/payments');
    expect(court.request.body).toEqual({ amountBaht: 300, method: 'Cash', note: undefined });
    court.flush(booking('b1', 'c1', [19], { toPayBaht: 0 }));

    const sale = httpMock.expectOne('/api/venues/v1/shop/sales');
    expect(sale.request.body).toEqual({
      lines: [{ itemId: 'i1', quantity: 2 }],
      paidBy: 'Cash',
      bookingId: 'b1',
    });
    sale.flush({});

    // The day is read again once the money is in.
    httpMock
      .expectOne((request) => request.url === '/api/venues/v1/bookings')
      .flush([booking('b1', 'c1', [19], { toPayBaht: 0, can: { takeMoney: false } })]);
    fixture.detectChanges();
    expect(textOf(fixture, 'due')).toBe('฿0');
  });
});
