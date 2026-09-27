import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { WEEKDAYS } from '../../core/venues/court.service';
import { clickOn, elementOf, pageProviders, textOf } from '../../testing/dom';
import { PricingPage } from './pricing.page';

describe('PricingPage', () => {
  let fixture: ComponentFixture<PricingPage>;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [PricingPage], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  /** The seed's venue: open 18:00–21:00 every day, 200 until 19:00 and 300 after. */
  function render(role: 'Owner' | 'Staff' = 'Owner'): void {
    fixture = TestBed.createComponent(PricingPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock.expectOne('/api/venues/v1/prices').flush({
      id: 'p1',
      createdAt: '2026-09-01T00:00:00Z',
      bands: WEEKDAYS.flatMap((day) => [
        { day, fromHour: 18, toHour: 19, bahtPerHour: 200 },
        { day, fromHour: 19, toHour: 21, bahtPerHour: 300 },
      ]),
    });
    httpMock.expectOne('/api/venues/v1/opening-hours').flush([
      {
        id: 'w1',
        effectiveFrom: '2026-09-01',
        inForce: true,
        days: WEEKDAYS.map((day) => ({ day, opensHour: 18, closesHour: 21 })),
      },
    ]);
    httpMock
      .expectOne('/api/venues/mine')
      .flush([{ id: 'v1', name: 'Smash', role, permissions: [] }]);
    fixture.detectChanges();
  }

  it("reads the venue's prices as tiers, with a peak to spare", () => {
    render();

    expect(textOf(fixture, 'tier-price-0')).toContain('200');
    expect(textOf(fixture, 'tier-price-1')).toContain('300');
    expect(textOf(fixture, 'tier-price-2')).toContain('360');
  });

  it('paints with the tier in hand and saves the week as bands', () => {
    render();

    // The tier itself, not a button inside it: clickOn presses the first button in a host.
    elementOf<HTMLElement>(fixture, 'tier-2')!.click();
    fixture.detectChanges();
    elementOf(fixture, 'cell-Saturday-20')!.dispatchEvent(new Event('pointerdown'));
    fixture.detectChanges();
    clickOn(fixture, 'tier-more-2');
    fixture.detectChanges();
    clickOn(fixture, 'pricing-save');

    const saved = httpMock.expectOne('/api/venues/v1/prices');
    expect(saved.request.method).toBe('PUT');
    const saturday = (saved.request.body.bands as { day: string }[]).filter(
      (band) => band.day === 'Saturday',
    );
    expect(saturday).toEqual([
      { day: 'Saturday', fromHour: 18, toHour: 19, bahtPerHour: 200 },
      { day: 'Saturday', fromHour: 19, toHour: 20, bahtPerHour: 300 },
      { day: 'Saturday', fromHour: 20, toHour: 21, bahtPerHour: 370 },
    ]);
    saved.flush({ id: 'p2', createdAt: '2026-09-28T00:00:00Z', bands: [] });
    fixture.detectChanges();

    expect(elementOf(fixture, 'pricing-saved')).not.toBeNull();
  });

  it('lets staff without settings read the week, and nothing more', () => {
    render('Staff');

    expect((elementOf(fixture, 'cell-Monday-18') as HTMLButtonElement).disabled).toBe(true);
    expect(elementOf(fixture, 'pricing-save')).toBeNull();
    expect(elementOf(fixture, 'tier-more-0')).toBeNull();
  });
});
