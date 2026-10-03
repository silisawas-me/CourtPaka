import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { clickOn, elementOf, pageProviders, textOf } from '../../testing/dom';
import { VenueCatalog } from './venue-catalog';
import { VenueCourts } from './venue-courts';
import { VenuePolicy } from './venue-policy';

function type(root: { nativeElement: HTMLElement }, testId: string, value: string): void {
  const input = root.nativeElement.querySelector(`[data-testid="${testId}"]`) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('settings tabs (thai-fit)', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [VenueCourts, VenueCatalog, VenuePolicy],
      providers: pageProviders(),
    });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('lists the courts, and says which bookings stand in the way of a closure', () => {
    const fixture = TestBed.createComponent(VenueCourts);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('canManage', true);
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1/courts').flush([
      { id: 'c1', name: 'Court 1', position: 1, isActive: true },
      { id: 'c2', name: 'Court 2', position: 2, isActive: false },
    ]);
    httpMock.expectOne('/api/venues/v1/closures').flush([]);
    fixture.detectChanges();

    expect(elementOf(fixture, 'court-state-c2')!.className).toContain('state-outOfUse');

    clickOn(fixture, 'court-close-c1');
    fixture.detectChanges();
    type(fixture, 'closure-reason', 'พื้นเปียก');
    fixture.detectChanges();
    clickOn(fixture, 'closure-save');

    httpMock.expectOne('/api/venues/v1/courts/c1/closures').flush(
      {
        code: 'closure.bookings_in_the_way',
        bookings: [
          {
            bookingId: 'b1',
            courtId: 'c1',
            courtName: 'Court 1',
            date: '2026-10-03',
            fromHour: 19,
            toHour: 20,
            status: 'Confirmed',
          },
        ],
      },
      { status: 409, statusText: 'Conflict' },
    );
    fixture.detectChanges();

    expect(textOf(fixture, 'closure-blocking')).toContain('19:00–20:00');
  });

  it('puts an offer on the board and stock on a shelf as one delivery', () => {
    const fixture = TestBed.createComponent(VenueCatalog);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('canManage', true);
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1/packages/types').flush([]);
    httpMock.expectOne('/api/venues/v1/shop/items').flush([
      {
        itemId: 'i1',
        name: 'Water',
        priceBaht: 15,
        unit: 'bottle',
        counted: true,
        tellMeAt: null,
        left: 3,
        runningLow: true,
        withdrawnAt: null,
      },
    ]);
    fixture.detectChanges();

    type(fixture, 'offer-name', '10 ชั่วโมง');
    type(fixture, 'offer-price', '1800');
    clickOn(fixture, 'offer-add');
    const offer = httpMock.expectOne('/api/venues/v1/packages/types');
    expect(offer.request.body).toEqual({
      name: '10 ชั่วโมง',
      hours: 10,
      priceBaht: 1800,
      validForDays: 90,
    });
    offer.flush({});
    httpMock.expectOne('/api/venues/v1/packages/types').flush([]);
    httpMock.expectOne('/api/venues/v1/shop/items').flush([]);
  });

  it('saves the cancellation steps most generous first', () => {
    const fixture = TestBed.createComponent(VenuePolicy);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('canManage', true);
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1/cancellation-policy').flush({
      id: 'p1',
      createdAt: '2026-10-01T00:00:00Z',
      tiers: [{ hoursBefore: 24, refundPercent: 50 }],
    });
    fixture.detectChanges();

    clickOn(fixture, 'policy-add');
    fixture.detectChanges();
    type(fixture, 'policy-hours-1', '48');
    type(fixture, 'policy-percent-1', '100');
    fixture.detectChanges();
    clickOn(fixture, 'policy-save');

    const saved = httpMock.expectOne('/api/venues/v1/cancellation-policy');
    expect(saved.request.method).toBe('PUT');
    expect(saved.request.body).toEqual({
      tiers: [
        { hoursBefore: 48, refundPercent: 100 },
        { hoursBefore: 24, refundPercent: 50 },
      ],
    });
  });
});
