import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { clickOn, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
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

  function grid() {
    const hours = Array.from({ length: 16 }, (_, index) => ({
      hour: 8 + index,
      status: 'Free',
      bahtPerHour: 8 + index >= 17 ? 320 : 220,
    }));
    return {
      venue: { id: 'v1', name: 'Ari' },
      date,
      lastBookableDate: date,
      opensHour: 8,
      closesHour: 24,
      courts: [
        { courtId: 'c1', name: 'Court 1', hours },
        { courtId: 'c2', name: 'Court 2', hours },
      ],
    };
  }

  function render(day: object[]): void {
    fixture = TestBed.createComponent(TimelinePage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === '/api/venues/v1/availability').flush(grid());
    httpMock.expectOne((request) => request.url === '/api/venues/v1/bookings').flush(day);
    httpMock.expectOne('/api/venues/v1/shop/items').flush([water]);
    fixture.detectChanges();
  }

  it('shows the other courts to move to at once, free ones as the server says', () => {
    render([
      booking('b1', 'c1', [19], {
        can: {
          checkIn: false,
          takeMoney: false,
          extend: false,
          moveCourt: true,
          cancelChoices: [],
        },
      }),
    ]);
    httpMock.expectOne('/api/venues/v1/bookings/b1/hours').flush({
      extend: null,
      move: { courts: [{ courtId: 'c2', courtName: 'Court 2', baht: null }] },
      shorten: null,
    });
    fixture.detectChanges();

    // No button to open them first: the design draws them open.
    expect(elementOf(fixture, 'move-open')).toBeNull();
    expect(elementOf<HTMLButtonElement>(fixture, 'move-c2')!.disabled).toBe(false);
    // A one-hour booking has no hour to give back.
    expect(elementOf<HTMLButtonElement>(fixture, 'shorten')!.disabled).toBe(true);
  });

  it('takes the last hour off through its door when the server offers it', () => {
    render([
      booking('b1', 'c1', [19, 20], {
        can: { checkIn: false, takeMoney: false, extend: true, moveCourt: true, cancelChoices: [] },
      }),
    ]);
    httpMock.expectOne('/api/venues/v1/bookings/b1/hours').flush({
      extend: null,
      move: null,
      shorten: { date, hour: 20, courtId: 'c1', baht: 300 },
    });
    fixture.detectChanges();

    clickOn(fixture, 'shorten');
    httpMock
      .expectOne(
        (request) =>
          request.method === 'POST' && request.url === '/api/venues/v1/bookings/b1/shorten',
      )
      .flush(booking('b1', 'c1', [19]));
    httpMock.expectOne((request) => request.url === '/api/venues/v1/availability').flush(grid());
    httpMock
      .expectOne((request) => request.url === '/api/venues/v1/bookings')
      .flush([booking('b1', 'c1', [19])]);
    fixture.detectChanges();
  });

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

  // Artboard b1: part of the money now, the rest stays owed.
  it('takes part of what the court owes', () => {
    render([booking('b1', 'c1', [19])]);

    setInput(fixture, '[data-testid="take-amount"]', '100');
    expect(textOf(fixture, 'due')).toBe('฿100');

    clickOn(fixture, 'pay-PromptPay');
    const court = httpMock.expectOne('/api/venues/v1/bookings/b1/payments');
    expect(court.request.body).toEqual({ amountBaht: 100, method: 'PromptPay', note: undefined });
    court.flush(booking('b1', 'c1', [19], { toPayBaht: 200 }));
    httpMock
      .expectOne((request) => request.url === '/api/venues/v1/bookings')
      .flush([booking('b1', 'c1', [19], { toPayBaht: 200 })]);
    fixture.detectChanges();

    // What is typed never runs past what is owed.
    setInput(fixture, '[data-testid="take-amount"]', '999');
    expect(textOf(fixture, 'due')).toBe('฿200');
  });

  // Artboard b2: who called it off, and what that gives back — the server's own numbers.
  it('cancels with the reason chosen, showing what it gives back', () => {
    const can = {
      checkIn: false,
      takeMoney: false,
      extend: false,
      moveCourt: false,
      noShow: false,
      cancel: true,
      cancelChoices: [
        { reason: 'CustomerRequest', refundBaht: 0, refundPercent: 50, underHours: 24 },
        { reason: 'VenueInitiated', refundBaht: 300, refundPercent: 100, underHours: null },
        { reason: 'PaymentNotReceived', refundBaht: 0, refundPercent: 0, underHours: null },
      ],
    };
    render([booking('b1', 'c1', [19], { toPayBaht: 0, takenBaht: 300, can })]);

    clickOn(fixture, 'cancel-open');
    fixture.detectChanges();

    // Each reason as artboard b2 writes it: what it means, and what share it gives back.
    expect(textOf(fixture, 'reason-note-CustomerRequest')).toBe(
      'ก่อนเล่นน้อยกว่า 24 ชม. · คืน 50%',
    );
    expect(textOf(fixture, 'reason-note-VenueInitiated')).toContain('คืนเต็ม');
    expect(textOf(fixture, 'cancel-reason-PaymentNotReceived')).toContain('—');
    expect(textOf(fixture, 'cancel-freed')).toBe('c1 ช่วง 19:00–20:00 จะกลับมาว่างให้ขายทันที');
    expect(elementOf<HTMLButtonElement>(fixture, 'confirm-cancel')!.disabled).toBe(true);

    clickOn(fixture, 'cancel-reason-VenueInitiated');
    fixture.detectChanges();
    expect(textOf(fixture, 'refund-due')).toBe('฿300');
    setInput(fixture, '[data-testid="cancel-note"]', 'น้ำรั่ว');
    clickOn(fixture, 'confirm-cancel');

    const cancel = httpMock.expectOne('/api/venues/v1/bookings/b1/cancel');
    expect(cancel.request.body).toEqual({
      reason: 'VenueInitiated',
      paymentReceived: null,
      note: 'น้ำรั่ว',
    });
    cancel.flush(
      booking('b1', 'c1', [19], { status: 'Cancelled', can: { ...can, cancel: false } }),
    );
    httpMock.expectOne((request) => request.url === '/api/venues/v1/availability').flush(grid());
    httpMock.expectOne((request) => request.url === '/api/venues/v1/bookings').flush([]);
    fixture.detectChanges();

    expect(elementOf(fixture, 'confirm-cancel')).toBeNull();
  });

  it('writes down a refund right after cancelling, as much as is owed unless somebody types less', () => {
    const can = {
      checkIn: false,
      takeMoney: false,
      noShow: false,
      cancel: true,
      cancelChoices: [{ reason: 'VenueInitiated', refundBaht: 300 }],
    };
    render([booking('b1', 'c1', [19], { toPayBaht: 0, paymentState: 'Received', can })]);

    clickOn(fixture, 'cancel-open');
    fixture.detectChanges();
    clickOn(fixture, 'cancel-reason-VenueInitiated');
    fixture.detectChanges();
    clickOn(fixture, 'confirm-cancel');
    const cancelled = booking('b1', 'c1', [19], {
      status: 'Cancelled',
      toPayBaht: 0,
      refundDueBaht: 300,
      outstandingBaht: 300,
      can: { ...can, cancel: false, cancelChoices: [] },
    });
    httpMock.expectOne('/api/venues/v1/bookings/b1/cancel').flush(cancelled);
    httpMock.expectOne((request) => request.url === '/api/venues/v1/availability').flush(grid());
    httpMock.expectOne((request) => request.url === '/api/venues/v1/bookings').flush([cancelled]);
    fixture.detectChanges();

    clickOn(fixture, 'refund-open');
    httpMock.expectOne('/api/venues/v1/bookings/b1/refunds').flush({
      refundDueBaht: 300,
      sentBackBaht: 0,
      outstandingBaht: 300,
      records: [],
      yourLimitBaht: 500,
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'refund-left')).toBe('฿300');
    expect(textOf(fixture, 'refund-limit')).toContain('500');
    setInput(fixture, '[data-testid="refund-amount"]', '200');
    clickOn(fixture, 'refund-method-Cash');
    fixture.detectChanges();
    clickOn(fixture, 'confirm-refund');

    const record = httpMock.expectOne(
      (request) =>
        request.method === 'POST' && request.url === '/api/venues/v1/bookings/b1/refunds',
    );
    expect(record.request.body).toEqual({
      amountBaht: 200,
      refundedOn: date,
      method: 'Cash',
      note: null,
    });
    record.flush({
      refundDueBaht: 300,
      sentBackBaht: 200,
      outstandingBaht: 100,
      records: [],
      yourLimitBaht: 500,
    });
    httpMock.expectOne((request) => request.url === '/api/venues/v1/bookings').flush([]);
    fixture.detectChanges();

    expect(elementOf(fixture, 'confirm-refund')).toBeNull();
  });

  it('marks a no-show through its door, and says when a shut one opens', () => {
    render([
      booking('b1', 'c1', [19], {
        graceEndsAt: '2026-09-30T12:15:00Z',
        can: { checkIn: true, takeMoney: false, noShow: false, cancel: true, cancelChoices: [] },
      }),
    ]);

    // 18:45 now; the grace runs out at 19:15.
    expect(elementOf<HTMLButtonElement>(fixture, 'no-show')!.disabled).toBe(true);
    expect(textOf(fixture, 'no-show')).toContain('19:15');
  });
});
