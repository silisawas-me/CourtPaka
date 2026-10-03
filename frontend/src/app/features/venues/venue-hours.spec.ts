import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { clickOn, elementOf, pageProviders, textOf } from '../../testing/dom';
import { VenueHours } from './venue-hours';

describe('VenueHours', () => {
  let fixture: ComponentFixture<VenueHours>;
  let httpMock: HttpTestingController;

  const monday = { day: 'Monday', opensHour: 8, closesHour: 23 };

  beforeEach(() => {
    TestBed.configureTestingModule({ imports: [VenueHours], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(VenueHours);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('canManage', true);
    fixture.componentRef.setInput('venue', { id: 'v1', name: 'Ari', graceMinutes: 15 });
    fixture.detectChanges();
    httpMock
      .expectOne('/api/venues/v1/opening-hours')
      .flush([{ id: 's1', effectiveFrom: '2026-01-01', inForce: true, days: [monday] }]);
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  function choose(testId: string, value: string): void {
    const select = elementOf<HTMLSelectElement>(fixture, testId)!;
    select.value = value;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  it('prices the hour it opens before it saves the week that opens it', () => {
    choose('opens-Monday', '7');
    clickOn(fixture, 'hours-save');

    httpMock.expectOne('/api/venues/v1/prices').flush({
      id: 'p1',
      createdAt: '2026-01-01T00:00:00Z',
      bands: [{ day: 'Monday', fromHour: 8, toHour: 23, bahtPerHour: 180 }],
    });
    const prices = httpMock.expectOne(
      (request) => request.method === 'PUT' && request.url === '/api/venues/v1/prices',
    );
    expect(prices.request.body.bands).toContainEqual({
      day: 'Monday',
      fromHour: 7,
      toHour: 8,
      bahtPerHour: 180,
    });
    prices.flush({});

    const week = httpMock.expectOne(
      (request) => request.method === 'PUT' && request.url === '/api/venues/v1/opening-hours',
    );
    expect(week.request.body.days.find((one: { day: string }) => one.day === 'Monday')).toEqual({
      day: 'Monday',
      opensHour: 7,
      closesHour: 23,
    });
    week.flush({});
    fixture.detectChanges();

    expect(textOf(fixture, 'hours-result')).toContain('1');
  });

  it('names each day in words, and says a 24:00 close is midnight', () => {
    // The day names once came from a key deleted with the old settings page, and the rows read
    // "settings.day.Monday" until somebody looked (2026-10-03).
    expect(textOf(fixture, 'hours-Monday')).toContain('จันทร์');
    const closes = elementOf(fixture, 'closes-Monday') as HTMLSelectElement;
    expect(closes.options[closes.options.length - 1].textContent).toContain('(เที่ยงคืน)');
  });

  it('shuts a day, and opens a shut one as the rest of the week opens', () => {
    clickOn(fixture, 'toggle-Sunday');
    fixture.detectChanges();
    expect(elementOf<HTMLSelectElement>(fixture, 'opens-Sunday')!.value).toBe('8');

    clickOn(fixture, 'toggle-Monday');
    fixture.detectChanges();
    expect(elementOf<HTMLSelectElement>(fixture, 'opens-Monday')!.disabled).toBe(true);
  });

  it('refuses a day that closes before it opens', () => {
    choose('closes-Monday', '6');
    expect(elementOf(fixture, 'hours-invalid')).not.toBeNull();
    expect(elementOf<HTMLButtonElement>(fixture, 'hours-save')!.disabled).toBe(true);
  });

  it('sets the grace for latecomers', () => {
    expect(textOf(fixture, 'grace-minutes')).toContain('15');
    clickOn(fixture, 'grace-more');
    clickOn(fixture, 'grace-more');
    fixture.detectChanges();
    expect(textOf(fixture, 'grace-minutes')).toContain('25');
    clickOn(fixture, 'grace-30');
    clickOn(fixture, 'grace-save');

    const grace = httpMock.expectOne('/api/venues/v1/grace');
    expect(grace.request.body).toEqual({ minutes: 30 });
    grace.flush(null);
    fixture.detectChanges();
    expect(elementOf(fixture, 'grace-saved')).not.toBeNull();
  });
});
