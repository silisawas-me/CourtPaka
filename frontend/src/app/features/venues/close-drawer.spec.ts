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
      opensHour: 8,
      lines: [
        line({
          at: `${today}T02:00:00Z`,
          kind: 'Court',
          method: 'Cash',
          amountBaht: 260,
          who: 'คุณวิทย์',
          courts: 'คอร์ต 1',
          bookingKind: 'WalkIn',
          by: 'บอม',
        }),
        line({
          at: `${today}T02:30:00Z`,
          kind: 'Sale',
          method: 'Cash',
          amountBaht: 60,
          items: [{ name: 'น้ำดื่ม', quantity: 4 }],
        }),
        line({
          at: `${today}T03:00:00Z`,
          kind: 'Court',
          method: 'BankTransfer',
          amountBaht: 150,
          who: 'คุณบอส',
          courts: 'คอร์ต 2',
          bookingKind: 'WalkIn',
          part: 'Deposit',
          bookingId: 'b2',
        }),
        line({
          at: `${today}T03:30:00Z`,
          kind: 'Sale',
          method: 'TrueMoney',
          amountBaht: 50,
          items: [{ name: 'เกลือแร่', quantity: 2 }],
        }),
        line({
          at: `${today}T04:00:00Z`,
          out: true,
          kind: 'PaidOut',
          method: 'Cash',
          amountBaht: 50,
          spendKind: 'Repairs',
          note: 'ค่าซ่อมไฟคอร์ต 6',
          by: 'เดโม่',
        }),
      ],
      ...overrides,
    };
  }

  function line(fields: object) {
    return {
      out: false,
      who: null,
      courts: null,
      bookingKind: null,
      part: null,
      items: null,
      packageHours: null,
      spendKind: null,
      note: null,
      by: null,
      bookingId: null,
      ...fields,
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
    expect(textOf(fixture, 'close-difference')).toBe('−฿10');
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

    // The last shift handed its takings in and left its float: the next one starts with that.
    expect(elementOf<HTMLInputElement>(fixture, 'close-float')!.value).toBe('500');
    expect(textOf(fixture, 'close-count-0')).toContain('ปุ้ย');

    type('close-counted', '850');
    clickOn(fixture, 'close-shift');

    const sent = httpMock.expectOne((request) => request.url === '/api/venues/v1/money/closing');
    expect(sent.request.body).toEqual({
      openingFloatBaht: 500,
      countedCashBaht: 850,
      note: undefined,
      endsDay: false,
    });
    sent.flush({});
    httpMock.expectOne((request) => request.url === '/api/venues/v1/money').flush(money());
  });

  it('writes each cash row as what it was for and whose, with money out marked', () => {
    render();

    const rows = textOf(fixture, 'close-cash-rows');
    expect(rows).toContain('ค่าคอร์ต · Walk-in');
    expect(rows).toContain('คุณวิทย์ · คอร์ต 1 · รับโดย บอม');
    expect(rows).toContain('ขายของ · น้ำดื่ม ×4');
    expect(rows).toContain('ค่าซ่อมไฟคอร์ต 6');
    expect(rows).toContain('ค่าซ่อม · บันทึกโดย เดโม่');
    expect(rows).toContain('−฿50');
    // Only cash is in the drawer: the transfer and TrueMoney rows are tiles, not rows.
    expect(rows).not.toContain('คุณบอส');
    expect(textOf(fixture, 'close-taken')).toBe('฿520');
  });

  it('points at rows for exactly the difference while the count is still being typed', () => {
    render(money({ owing: [{ bookingId: 'b5', baht: 60, who: 'คุณแนน', courts: 'คอร์ต 5' }] }));

    // Before anything is typed, the three places to look are named and nothing is pointed at.
    expect(textOf(fixture, 'close-leads-sub')).toBe(TRANSLATIONS.th['closing.leadsWaiting']);
    expect(textOf(fixture, 'close-lead-none-StillOwed')).toContain(
      TRANSLATIONS.th['closing.groupHint.StillOwed'],
    );

    type('close-float', '1000');
    type('close-counted', '1190');

    expect(textOf(fixture, 'close-lead-CashTaken')).toContain('น้ำดื่ม ×4 · ฿60');
    expect(textOf(fixture, 'close-lead-StillOwed')).toContain('คุณแนน · คอร์ต 5 · ค้าง ฿60');
    expect(elementOf(fixture, 'close-lead-none-CashOut')).not.toBeNull();
  });

  it('after a count that came out short, says where the difference may be', () => {
    const shift = {
      date: today,
      openingFloatBaht: 1_000,
      expectedCashBaht: 1_310,
      countedCashBaht: 1_250,
      differenceBaht: -60,
      note: null,
      closedAt: `${today}T05:00:00Z`,
      endsDay: false,
      closedBy: 'บอม',
      from: `${today}T01:00:00Z`,
    };
    render(
      money({
        counts: [shift],
        openShift: { from: shift.closedAt, cashInBaht: 0, cashOutBaht: 0 },
        leads: [
          {
            kind: 'CashTaken',
            amountBaht: 60,
            bookingId: null,
            at: `${today}T02:30:00Z`,
            note: null,
          },
        ],
      }),
    );

    expect(textOf(fixture, 'close-leads')).toContain('฿60');
    expect(textOf(fixture, 'close-lead-CashTaken')).toContain('น้ำดื่ม ×4 · ฿60');
    // Kinds with nothing for that amount still show, dashed, so nobody goes looking there.
    expect(textOf(fixture, 'close-leads')).toContain(
      TRANSLATIONS.th['closing.leadGroup.StillOwed'],
    );
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
