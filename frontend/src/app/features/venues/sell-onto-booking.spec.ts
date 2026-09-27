import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { clickOn, elementOf, pageProviders, textOf } from '../../testing/dom';
import { SellOntoBooking } from './sell-onto-booking';

function item(overrides: Record<string, unknown> = {}) {
  return {
    itemId: 'i1',
    name: 'ลูกขนไก่',
    priceBaht: 90,
    unit: 'หลอด',
    counted: true,
    tellMeAt: 3,
    left: 2,
    runningLow: false,
    withdrawnAt: null,
    ...overrides,
  };
}

describe('SellOntoBooking', () => {
  let fixture: ComponentFixture<SellOntoBooking>;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [SellOntoBooking], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(SellOntoBooking);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('bookingId', 'b1');
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  function openShop(items: object[]): void {
    clickOn(fixture, 'sell-open');
    httpMock.expectOne('/api/venues/v1/shop/items').flush(items);
    fixture.detectChanges();
  }

  it('does not ask for the shop until somebody wants to sell', () => {
    // No request yet: most bookings are opened to check somebody in.
    httpMock.expectNone('/api/venues/v1/shop/items');
    expect(elementOf(fixture, 'sell-open')).not.toBeNull();
  });

  it('offers only what is on sale and on the shelf', () => {
    openShop([
      item(),
      item({ itemId: 'gone', withdrawnAt: '2026-09-01T00:00:00Z' }),
      item({ itemId: 'out', left: 0 }),
      item({ itemId: 'racket', counted: false, left: null }),
    ]);

    expect(elementOf(fixture, 'sell-item-i1')).not.toBeNull();
    expect(elementOf(fixture, 'sell-item-racket')).not.toBeNull();
    expect(elementOf(fixture, 'sell-item-gone')).toBeNull();
    expect(elementOf(fixture, 'sell-item-out')).toBeNull();
  });

  it('never counts past what the shelf holds', () => {
    openShop([item({ left: 2 })]);

    for (let press = 0; press < 3; press++) {
      clickOn(fixture, 'sell-more-i1');
    }
    fixture.detectChanges();

    expect(textOf(fixture, 'sell-count-i1')).toBe('2');
  });

  it('sells with the booking attached and the way it was paid', () => {
    openShop([item()]);
    clickOn(fixture, 'sell-more-i1');
    fixture.detectChanges();
    clickOn(fixture, 'sell-paid-PromptPay');
    fixture.detectChanges();
    clickOn(fixture, 'sell-confirm');

    const request = httpMock.expectOne('/api/venues/v1/shop/sales');
    expect(request.request.body).toEqual({
      lines: [{ itemId: 'i1', quantity: 1 }],
      paidBy: 'PromptPay',
      bookingId: 'b1',
    });
    request.flush({
      saleId: 's1',
      bookingId: 'b1',
      totalBaht: 90,
      soldAt: '2026-09-28T12:00:00Z',
      cancelledAt: null,
      cancelReason: null,
      lines: [],
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'sell-done')).toContain('90');
    // The shelf moved, so the list is asked for again rather than guessed at.
    expect(elementOf(fixture, 'sell-open')).not.toBeNull();
  });
});
