import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { clickOn, elementOf, pageProviders, setInput, textOf } from '../../testing/dom';
import { TRANSLATIONS } from '../../testing/translations';
import { ShopPage } from './shop.page';

function item(overrides: Record<string, unknown> = {}) {
  return {
    itemId: 'i1',
    name: 'ลูกขนไก่',
    priceBaht: 90,
    unit: 'ลูก',
    counted: true,
    tellMeAt: 3,
    left: 10,
    runningLow: false,
    withdrawnAt: null,
    ...overrides,
  };
}

function sale(overrides: Record<string, unknown> = {}) {
  return {
    saleId: 's1',
    bookingId: null,
    totalBaht: 180,
    soldAt: '2026-09-26T04:00:00Z',
    cancelledAt: null,
    cancelReason: null,
    lines: [{ itemId: 'i1', name: 'ลูกขนไก่', quantity: 2, eachBaht: 90 }],
    ...overrides,
  };
}

describe('ShopPage', () => {
  let fixture: ComponentFixture<ShopPage>;
  let httpMock: HttpTestingController;

  function render(items: unknown[], sales: unknown[] = [], spending: unknown[] = []): void {
    // The language is remembered in storage, and a spec that ran earlier in this worker may have
    // left English there; these assertions read the Thai words.
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [ShopPage],
      providers: pageProviders(),
    });

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ShopPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock.expectOne('/api/venues/v1/shop/items').flush(items);
    httpMock.expectOne((request) => request.url === '/api/venues/v1/shop/sales').flush(sales);
    httpMock.expectOne((request) => request.url === '/api/venues/v1/spending').flush(spending);
    fixture.detectChanges();
  }

  afterEach(() => {
    httpMock.verify();
    TestBed.resetTestingModule();
  });

  it('says when there is nothing to sell', () => {
    render([]);

    expect(textOf(fixture, 'nothing-on-the-board')).toBe(TRANSLATIONS.th['shop.nothingOnTheBoard']);
  });

  it('rings things up one press at a time and adds them up', () => {
    render([item()]);

    clickOn(fixture, 'more-i1');
    clickOn(fixture, 'more-i1');
    fixture.detectChanges();

    expect(textOf(fixture, 'basket-i1')).toContain('2');
    expect(textOf(fixture, 'basket-total')).toContain('180');

    clickOn(fixture, 'less-i1');
    fixture.detectChanges();
    expect(textOf(fixture, 'basket-i1')).toContain('1');
  });

  /** A counter should not be able to ring up what it cannot hand over (PRD US-32). */
  it('will not ring up more than there are', () => {
    render([item({ left: 2 })]);

    clickOn(fixture, 'more-i1');
    clickOn(fixture, 'more-i1');
    fixture.detectChanges();

    expect((elementOf(fixture, 'more-i1') as HTMLButtonElement).disabled).toBe(true);
    expect(textOf(fixture, 'basket-i1')).toContain('2');
  });

  it('takes the money, says how it came in, and takes the stock off the shelf', () => {
    render([item()]);

    clickOn(fixture, 'more-i1');
    clickOn(fixture, 'paid-PromptPay');
    clickOn(fixture, 'take-money');

    const request = httpMock.expectOne('/api/venues/v1/shop/sales');
    expect(request.request.body).toEqual({
      lines: [{ itemId: 'i1', quantity: 1 }],
      paidBy: 'PromptPay',
      bookingId: null,
    });

    request.flush(
      sale({
        totalBaht: 90,
        lines: [{ itemId: 'i1', name: 'ลูกขนไก่', quantity: 1, eachBaht: 90 }],
      }),
    );
    fixture.detectChanges();

    // The sale's own lines say what left the shelf, so the counter's next press is against the
    // right number without a second read of the board.
    expect(elementOf(fixture, 'sale-s1')).not.toBeNull();
    expect(textOf(fixture, 'left-i1')).toContain('9');
  });

  /** Few enough left that somebody should order more (PRD US-33). */
  it('marks what is running low', () => {
    render([item({ left: 2, runningLow: true })]);

    expect(elementOf(fixture, 'item-i1')!.classList).toContain('low');
    expect(textOf(fixture, 'running-low')).toContain('1');
  });

  it('takes a sale back and puts the stock on the shelf again', () => {
    render([item()], [sale()]);

    clickOn(fixture, 'take-back-s1');

    const request = httpMock.expectOne('/api/venues/v1/shop/sales/s1/cancel');
    expect(request.request.body.reason).toBe(TRANSLATIONS.th['shop.takenBack']);

    request.flush(sale({ cancelledAt: '2026-09-26T05:00:00Z', cancelReason: 'คืน' }));
    fixture.detectChanges();

    expect(elementOf(fixture, 'taken-back-s1')).not.toBeNull();
    expect(textOf(fixture, 'left-i1')).toContain('12');
  });

  it('writes down money paid out, and lets the server say what day it is', () => {
    render([item()]);

    clickOn(fixture, 'kind-Utilities');
    setInput(fixture, '[data-testid="spend-amount"]', '450');
    setInput(fixture, '[data-testid="spend-note"]', 'ค่าน้ำ');
    clickOn(fixture, 'spend-by-Cash');
    clickOn(fixture, 'record-spend');

    const request = httpMock.expectOne('/api/venues/v1/spending');
    expect(request.request.body).toEqual({
      kind: 'Utilities',
      amountBaht: 450,
      paidOn: null,
      paidBy: 'Cash',
      note: 'ค่าน้ำ',
      itemId: null,
      quantity: null,
    });

    request.flush({
      spendId: 'sp1',
      kind: 'Utilities',
      amountBaht: 450,
      paidOn: '2026-09-26',
      paidBy: 'Cash',
      note: 'ค่าน้ำ',
      voidedAt: null,
      voidReason: null,
    });
    fixture.detectChanges();

    expect(elementOf(fixture, 'spend-sp1')).not.toBeNull();
    expect(textOf(fixture, 'spent-this-month')).toContain('450');
  });

  /** A delivery is one expense that also fills a shelf, said once (PRD US-33). */
  it('buys stock in as one expense that fills the shelf', () => {
    render([item()]);

    setInput(fixture, '[data-testid="spend-amount"]', '700');
    clickOn(fixture, 'kind-Stock');
    fixture.detectChanges();

    clickOn(fixture, 'bought-i1');
    fixture.detectChanges();
    setInput(fixture, '[data-testid="bought-how-many"]', '10');
    clickOn(fixture, 'record-spend');

    const request = httpMock.expectOne('/api/venues/v1/spending');
    expect(request.request.body.itemId).toBe('i1');
    expect(request.request.body.quantity).toBe(10);

    request.flush({
      spendId: 'sp2',
      kind: 'Stock',
      amountBaht: 700,
      paidOn: '2026-09-26',
      paidBy: 'Cash',
      note: null,
      voidedAt: null,
      voidReason: null,
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'left-i1')).toContain('20');
  });

  /**
   * Writing an expense down and reading the month's report are two permissions (PRD US-33), so a
   * counter that only has the first still has a shop to run.
   */
  it('still works for somebody who may not read what the venue spent', () => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [ShopPage], providers: pageProviders() });

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ShopPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock.expectOne('/api/venues/v1/shop/items').flush([item()]);
    httpMock.expectOne((request) => request.url === '/api/venues/v1/shop/sales').flush([]);
    httpMock
      .expectOne((request) => request.url === '/api/venues/v1/spending')
      .flush({ code: 'venue.forbidden' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    // The board is there, the basket works, and nothing says the page failed.
    expect(elementOf(fixture, 'page-error')).toBeNull();
    expect(elementOf(fixture, 'item-i1')).not.toBeNull();
    clickOn(fixture, 'more-i1');
    fixture.detectChanges();
    expect(textOf(fixture, 'basket-i1')).toContain('1');

    // What is hidden is the report, not the form: they may still write one down.
    expect(elementOf(fixture, 'spent-this-month')).toBeNull();
    expect(elementOf(fixture, 'record-spend')).not.toBeNull();
  });

  /** Half a purchase — the money without the goods — is what the server refuses when it is told
   * about it, and cannot see when the quantity is quietly left at zero (PRD US-33). */
  it('will not buy stock in without saying how many arrived', () => {
    render([item()]);

    setInput(fixture, '[data-testid="spend-amount"]', '700');
    clickOn(fixture, 'kind-Stock');
    fixture.detectChanges();
    clickOn(fixture, 'bought-i1');
    fixture.detectChanges();

    clickOn(fixture, 'record-spend');

    httpMock.expectNone('/api/venues/v1/spending');
    expect(textOf(fixture, 'bought-how-many-error')).not.toBe('');
  });

  it('will not write down a payment of nothing', () => {
    render([item()]);

    setInput(fixture, '[data-testid="spend-amount"]', '0');
    clickOn(fixture, 'record-spend');

    httpMock.expectNone('/api/venues/v1/spending');
    expect(textOf(fixture, 'spend-amount-error')).not.toBe('');
  });

  it('puts something on the board and takes it off', () => {
    render([]);

    setInput(fixture, '[data-testid="item-name"]', 'น้ำเปล่า');
    setInput(fixture, '[data-testid="item-price"]', '15');
    setInput(fixture, '[data-testid="item-unit"]', 'ขวด');
    clickOn(fixture, 'add-item');

    const added = httpMock.expectOne('/api/venues/v1/shop/items');
    expect(added.request.body).toEqual({
      name: 'น้ำเปล่า',
      priceBaht: 15,
      unit: 'ขวด',
      counted: true,
      tellMeAt: 3,
    });

    added.flush(item({ itemId: 'i2', name: 'น้ำเปล่า', priceBaht: 15, unit: 'ขวด', left: 0 }));
    fixture.detectChanges();

    expect(elementOf(fixture, 'board-i2')).not.toBeNull();

    clickOn(fixture, 'withdraw-i2');
    httpMock
      .expectOne('/api/venues/v1/shop/items/i2/withdraw')
      .flush(item({ itemId: 'i2', name: 'น้ำเปล่า', withdrawnAt: '2026-09-26T06:00:00Z' }));
    fixture.detectChanges();

    expect(elementOf(fixture, 'board-i2')!.classList).toContain('old');
  });

  /** What the shelf actually holds, and why the ledger said otherwise (PRD US-33). */
  it('counts a shelf and says why it did not match', () => {
    render([item()]);

    clickOn(fixture, 'count-i1');
    fixture.detectChanges();

    // It opens on what the ledger says, because most counts agree with it.
    expect((elementOf(fixture, 'count-counted') as HTMLInputElement).value).toBe('10');

    setInput(fixture, '[data-testid="count-counted"]', '8');
    setInput(fixture, '[data-testid="count-reason"]', 'หายไปสองลูก');
    clickOn(fixture, 'save-count');

    const request = httpMock.expectOne('/api/venues/v1/shop/items/i1/count');
    expect(request.request.body).toEqual({ counted: 8, reason: 'หายไปสองลูก' });

    request.flush(item({ left: 8 }));
    fixture.detectChanges();

    expect(elementOf(fixture, 'counting')).toBeNull();
    expect(textOf(fixture, 'left-i1')).toContain('8');
  });

  it('will not save a count with no reason', () => {
    render([item()]);

    clickOn(fixture, 'count-i1');
    fixture.detectChanges();
    setInput(fixture, '[data-testid="count-counted"]', '8');
    clickOn(fixture, 'save-count');

    httpMock.expectNone('/api/venues/v1/shop/items/i1/count');
    expect(textOf(fixture, 'count-reason-error')).not.toBe('');
  });
});
