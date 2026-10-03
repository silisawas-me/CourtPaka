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

  it('names each day in words, and says where a close at or past midnight lands', () => {
    // The day names once came from a key deleted with the old settings page, and the rows read
    // "settings.day.Monday" until somebody looked (2026-10-03).
    expect(textOf(fixture, 'hours-Monday')).toContain('จันทร์');
    const closes = elementOf<HTMLSelectElement>(fixture, 'closes-Monday')!;
    const labels = [...closes.options].map((option) => option.textContent!.trim());
    expect(labels).toContain('24:00 (เที่ยงคืน)');
    // The artboard's "02:00 ของวันเสาร์": past midnight is the next day's clock (thai-fit T4).
    expect(labels).toContain('02:00 ของวันอังคาร');
  });

  it('closes past midnight: the row says so, and the week is saved with the late hours', () => {
    choose('closes-Monday', '26');
    expect(elementOf(fixture, 'late-Monday')).not.toBeNull();
    expect(textOf(fixture, 'opening-hours')).toContain('จันทร์ 01:00–02:00');

    clickOn(fixture, 'hours-save');
    httpMock.expectOne('/api/venues/v1/prices').flush({
      id: 'p1',
      createdAt: '2026-01-01T00:00:00Z',
      bands: [{ day: 'Monday', fromHour: 8, toHour: 26, bahtPerHour: 180 }],
    });
    const week = httpMock.expectOne(
      (request) => request.method === 'PUT' && request.url === '/api/venues/v1/opening-hours',
    );
    expect(week.request.body.days.find((one: { day: string }) => one.day === 'Monday')).toEqual({
      day: 'Monday',
      opensHour: 8,
      closesHour: 26,
    });
    week.flush({});
  });

  it('refuses a day that opens before the night before has closed', () => {
    choose('closes-Monday', '26');
    choose('opens-Tuesday', '1');
    expect(elementOf(fixture, 'hours-too-early')).not.toBeNull();
    expect(elementOf<HTMLButtonElement>(fixture, 'hours-save')!.disabled).toBe(true);

    choose('opens-Tuesday', '2');
    expect(elementOf(fixture, 'hours-too-early')).toBeNull();
  });

  it('shuts a day from where it opens, and opens a shut one for an hour to start with', () => {
    choose('opens-Monday', '');
    expect(elementOf<HTMLSelectElement>(fixture, 'closes-Monday')!.disabled).toBe(true);

    choose('opens-Sunday', '9');
    expect(elementOf<HTMLSelectElement>(fixture, 'closes-Sunday')!.value).toBe('10');
  });
});
