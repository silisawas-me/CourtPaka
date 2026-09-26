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
    httpMock.expectOne((request) => request.url === '/api/venues/v1/shop/spending').flush(spending);
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

  it('takes the money and says how it came in', () => {
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

    // What is left has changed, so the board is read again.
    httpMock.expectOne('/api/venues/v1/shop/items').flush([item({ left: 9 })]);
    fixture.detectChanges();

    expect(elementOf(fixture, 'sale-s1')).not.toBeNull();
    expect(textOf(fixture, 'left-i1')).toContain('9');
  });

  /** Few enough left that somebody should order more (PRD US-33). */
  it('marks what is running low', () => {
    render([item({ left: 2, runningLow: true })]);

    expect(elementOf(fixture, 'item-i1')!.classList).toContain('low');
    expect(textOf(fixture, 'running-low')).toContain('1');
  });

  it('takes a sale back and reads the shelf and the spending again', () => {
    render([item()], [sale()]);

    clickOn(fixture, 'take-back-s1');

    const request = httpMock.expectOne('/api/venues/v1/shop/sales/s1/cancel');
    expect(request.request.body.reason).toBe(TRANSLATIONS.th['shop.takenBack']);

    request.flush(sale({ cancelledAt: '2026-09-26T05:00:00Z', cancelReason: 'คืน' }));
    httpMock.expectOne('/api/venues/v1/shop/items').flush([item({ left: 12 })]);
    httpMock.expectOne((request) => request.url === '/api/venues/v1/shop/spending').flush([]);
    fixture.detectChanges();

    expect(elementOf(fixture, 'taken-back-s1')).not.toBeNull();
  });

  it('writes down money paid out', () => {
    render([item()]);

    clickOn(fixture, 'kind-Utilities');
    setInput(fixture, '[data-testid="spend-amount"]', '450');
    setInput(fixture, '[data-testid="spend-note"]', 'ค่าน้ำ');
    clickOn(fixture, 'spend-by-Cash');
    clickOn(fixture, 'record-spend');

    const request = httpMock.expectOne('/api/venues/v1/shop/spending');
    expect(request.request.body.kind).toBe('Utilities');
    expect(request.request.body.amountBaht).toBe(450);
    expect(request.request.body.paidBy).toBe('Cash');
    expect(request.request.body.note).toBe('ค่าน้ำ');

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

  it('will not write down a payment of nothing', () => {
    render([item()]);

    setInput(fixture, '[data-testid="spend-amount"]', '0');
    clickOn(fixture, 'record-spend');

    httpMock.expectNone('/api/venues/v1/shop/spending');
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
});
