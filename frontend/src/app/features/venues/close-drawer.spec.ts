import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { venueNow } from '../../core/i18n/plain-date';
import { clickOn, elementOf, pageProviders, textOf } from '../../testing/dom';
import { TRANSLATIONS } from '../../testing/translations';
import { CloseDrawer } from './close-drawer';

describe('CloseDrawer', () => {
  let fixture: ComponentFixture<CloseDrawer>;
  let httpMock: HttpTestingController;
  const today = venueNow().date;

  function money(overrides: object = {}) {
    return {
      date: today,
      takenBaht: 1_000,
      cashBaht: 300,
      promptPayBaht: 400,
      cardBaht: 100,
      bankTransferBaht: 150,
      trueMoneyBaht: 50,
      cashRefundedBaht: 20,
      cashPaidOutBaht: 30,
      outstandingBaht: 0,
      cashReceipts: [],
      closed: null,
      leads: [],
      counts: [],
      openShift: { from: `${today}T01:00:00Z`, cashInBaht: 300, cashOutBaht: 50 },
      ...overrides,
    };
  }

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [CloseDrawer], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    fixture.destroy();
    httpMock.verify();
  });

  function render(answer: object = money()): void {
    fixture = TestBed.createComponent(CloseDrawer);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();
    httpMock.expectOne((request) => request.url === '/api/venues/v1/money').flush(answer);
    fixture.detectChanges();
  }

  function type(testId: string, value: string): void {
    const input = elementOf<HTMLInputElement>(fixture, testId)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  it('shows each form the money came in, with the bank and TrueMoney apart from the till', () => {
    render();

    expect(textOf(fixture, 'close-method-BankTransfer')).toContain('฿150');
    expect(textOf(fixture, 'close-method-TrueMoney')).toContain('฿50');
    expect(textOf(fixture, 'close-method-TrueMoney')).toContain(
      TRANSLATIONS.th['closing.notInTill'],
    );
  });

  it('works out what the shift should hold and says how far the count is out', () => {
    render();

    type('close-float', '1000');
    expect(textOf(fixture, 'close-expected')).toBe('฿1,250');

    type('close-counted', '1240');
    expect(textOf(fixture, 'close-difference')).toBe('฿10');
    expect(elementOf(fixture, 'close-difference')!.parentElement!.textContent).toContain(
      TRANSLATIONS.th['closing.short'],
    );
  });

  it('hands a shift over without closing the day, then reads the day again', () => {
    render(
      money({
        counts: [
          {
            date: today,
            openingFloatBaht: 500,
            expectedCashBaht: 600,
            countedCashBaht: 600,
            differenceBaht: 0,
            note: null,
            closedAt: `${today}T09:00:00Z`,
            endsDay: false,
            closedBy: 'ปุ้ย',
            from: `${today}T01:00:00Z`,
          },
        ],
      }),
    );

    // The next shift is handed what the last count left in the drawer.
    expect(elementOf<HTMLInputElement>(fixture, 'close-float')!.value).toBe('600');
    expect(textOf(fixture, 'close-count-0')).toContain('ปุ้ย');

    type('close-counted', '850');
    clickOn(fixture, 'close-shift');

    const sent = httpMock.expectOne((request) => request.url === '/api/venues/v1/money/closing');
    expect(sent.request.body).toEqual({
      openingFloatBaht: 600,
      countedCashBaht: 850,
      note: undefined,
      endsDay: false,
    });
    sent.flush({});
    httpMock.expectOne((request) => request.url === '/api/venues/v1/money').flush(money());
  });

  it('shows a closed day as counted, with nothing more to count', () => {
    const close = {
      date: today,
      openingFloatBaht: 600,
      expectedCashBaht: 750,
      countedCashBaht: 740,
      differenceBaht: -10,
      note: null,
      closedAt: `${today}T15:00:00Z`,
      endsDay: true,
      closedBy: 'บอม',
      from: `${today}T09:00:00Z`,
    };
    render(money({ closed: close, counts: [close], openShift: null }));

    expect(elementOf(fixture, 'close-closed')).not.toBeNull();
    expect(elementOf(fixture, 'close-counted')).toBeNull();
    expect(elementOf(fixture, 'close-open-shift')).toBeNull();
  });
});
