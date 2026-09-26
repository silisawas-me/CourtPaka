import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { plainDate, venueToday } from '../../core/i18n/plain-date';
import { TRANSLATIONS } from '../../testing/translations';
import { clickOn, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
import { MoneyPage } from './money.page';

/** The venue's own today, because that is the day the page asks for when the URL says nothing. */
const TODAY = plainDate(venueToday());

function day(overrides: Record<string, unknown> = {}) {
  return {
    date: TODAY,
    takenBaht: 900,
    cashBaht: 500,
    promptPayBaht: 300,
    cardBaht: 100,
    cashRefundedBaht: 0,
    cashPaidOutBaht: 0,
    outstandingBaht: 200,
    cashReceipts: [
      {
        id: 'r1',
        bookingId: 'b1',
        amountBaht: 500,
        method: 'Cash',
        receivedAt: '2026-09-23T11:00:00Z',
        note: 'มัดจำ',
      },
    ],
    closed: null,
    leads: [],
    ...overrides,
  };
}

function closing(overrides: Record<string, unknown> = {}) {
  return {
    date: TODAY,
    openingFloatBaht: 1000,
    expectedCashBaht: 1500,
    countedCashBaht: 1400,
    differenceBaht: -100,
    note: 'ขาดร้อยนึง',
    closedAt: '2026-09-23T15:00:00Z',
    ...overrides,
  };
}

describe('MoneyPage', () => {
  let fixture: ComponentFixture<MoneyPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();

    await TestBed.configureTestingModule({
      imports: [MoneyPage],
      providers: pageProviders([{ path: 'venues/:venueId', children: [] }]),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    fixture.destroy();
    httpMock.verify();
  });

  function render(answer: object = day(), date?: string): void {
    fixture = TestBed.createComponent(MoneyPage);
    fixture.componentRef.setInput('venueId', 'v1');
    if (date) {
      fixture.componentRef.setInput('date', date);
    }
    fixture.detectChanges();

    httpMock.expectOne((request) => request.url === '/api/venues/v1/money').flush(answer);
    fixture.detectChanges();
  }

  it('leads with what the day took and breaks it into the forms it came in', () => {
    render();

    expect(textOf(fixture, 'taken-today')).toContain('900');
    expect(textOf(fixture, 'cash')).toBe('500');
    expect(textOf(fixture, 'promptpay')).toBe('300');
    expect(textOf(fixture, 'card')).toBe('100');
    expect(textOf(fixture, 'outstanding')).toBe('200');
  });

  it('asks about the day the URL holds', () => {
    fixture = TestBed.createComponent(MoneyPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('date', '2026-09-20');
    fixture.detectChanges();

    const request = httpMock.expectOne((one) => one.url === '/api/venues/v1/money');
    expect(request.request.params.get('date')).toBe('2026-09-20');
    request.flush(day({ date: '2026-09-20' }));
  });

  it('drops an older answer once a newer day is asked for', () => {
    fixture = TestBed.createComponent(MoneyPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('date', '2026-09-20');
    fixture.detectChanges();
    const older = httpMock.expectOne((one) => one.params.get('date') === '2026-09-20');

    fixture.componentRef.setInput('date', '2026-09-21');
    fixture.detectChanges();
    const newer = httpMock.expectOne((one) => one.params.get('date') === '2026-09-21');

    expect(older.cancelled).toBe(true);
    newer.flush(day({ date: '2026-09-21' }));
  });

  it('lists only the cash, because only the cash is in the till', () => {
    render();

    expect(elementOf(fixture, 'receipt-r1')).not.toBeNull();
    expect(textOf(fixture, 'receipt-r1')).toContain('500');
  });

  it('says so when nothing has come in rather than showing an empty table', () => {
    render(day({ cashReceipts: [], takenBaht: 0 }));

    expect(elementOf(fixture, 'receipts')).toBeNull();
    expect(textOf(fixture, 'no-receipts')).toBe(TRANSLATIONS.th['money.nothing']);
  });

  it('counts the till with what was typed and shows the count the server worked out', () => {
    render();

    setInput(fixture, '#float', '1000');
    setInput(fixture, '#counted', '1400');
    setInput(fixture, '#closing-note', 'ขาดร้อยนึง');
    clickOn(fixture, 'close-day');

    const request = httpMock.expectOne((one) => one.url === '/api/venues/v1/money/closing');
    expect(request.request.body).toEqual({
      openingFloatBaht: 1000,
      countedCashBaht: 1400,
      note: 'ขาดร้อยนึง',
    });
    expect(request.request.params.get('date')).toBe(TODAY);
    request.flush(closing());
    fixture.detectChanges();

    // Counting the day changes what the server says about it — which rows would explain the
    // difference come with the day — so the day is read again rather than patched.
    httpMock
      .expectOne((one) => one.url === '/api/venues/v1/money')
      .flush(day({ closed: closing() }));
    fixture.detectChanges();

    // The difference is the server's, not the page's, and a till that is short says so.
    expect(textOf(fixture, 'closed-expected')).toBe('1,500');
    expect(textOf(fixture, 'closed-difference')).toContain(TRANSLATIONS.th['money.short']);
    expect(elementOf(fixture, 'close-day')).toBeNull();
  });

  it('says where to look when the till did not come out even', () => {
    render(
      day({
        closed: closing(),
        leads: [
          {
            kind: 'CashTaken',
            amountBaht: 100,
            bookingId: 'b1',
            at: '2026-09-23T11:00:00Z',
            note: 'มัดจำ',
          },
          { kind: 'StillOwed', amountBaht: 100, bookingId: 'b2', at: null, note: null },
        ],
      }),
    );

    expect(elementOf(fixture, 'leads')).not.toBeNull();
    expect(textOf(fixture, 'lead-CashTaken')).toContain(TRANSLATIONS.th['money.lead.CashTaken']);
    expect(textOf(fixture, 'lead-StillOwed')).toContain('100');
  });

  it('offers nothing about a count that balanced', () => {
    render(day({ closed: closing({ differenceBaht: 0, countedCashBaht: 1500 }), leads: [] }));

    expect(elementOf(fixture, 'leads')).toBeNull();
  });

  it('does not ask twice: a day that has been counted shows its count', () => {
    render(day({ closed: closing({ differenceBaht: 0, countedCashBaht: 1500, note: null }) }));

    expect(elementOf(fixture, 'close-day')).toBeNull();
    expect(textOf(fixture, 'closed-difference')).toBe(TRANSLATIONS.th['money.exact']);
  });

  it('will not send half a count', () => {
    render();

    setInput(fixture, '#counted', '1400');
    clickOn(fixture, 'close-day');

    httpMock.expectNone((one) => one.url === '/api/venues/v1/money/closing');
  });

  it('says what a refused count was refused for', () => {
    render();

    setInput(fixture, '#float', '1000');
    setInput(fixture, '#counted', '1400');
    clickOn(fixture, 'close-day');

    httpMock
      .expectOne((one) => one.url === '/api/venues/v1/money/closing')
      .flush({ code: 'money.already_closed' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'close-error')).toBe(TRANSLATIONS.th['error.money.already_closed']);
  });

  it('tells a member without the reports permission what they are missing', () => {
    fixture = TestBed.createComponent(MoneyPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock
      .expectOne((one) => one.url === '/api/venues/v1/money')
      .flush(null, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(textOf(fixture, 'page-error')).toBe(TRANSLATIONS.th['money.noPermission']);
  });
});
