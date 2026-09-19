import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { check, elementOf, pageProviders, setInput, submitForm, textOf } from '../../testing/dom';
import { PriceBands } from './price-bands';

const WEEKDAY_BANDS = [
  { day: 'Monday', fromHour: 6, toHour: 18, bahtPerHour: 200 },
  { day: 'Monday', fromHour: 18, toHour: 22, bahtPerHour: 300 },
];

describe('PriceBands', () => {
  let fixture: ComponentFixture<PriceBands>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [PriceBands],
      providers: pageProviders(),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  function render(bands: unknown[] | null, canManage = true): void {
    fixture = TestBed.createComponent(PriceBands);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('canManage', canManage);
    fixture.detectChanges();

    const request = httpMock.expectOne('/api/venues/v1/prices');
    if (bands === null) {
      // The server answers 204 for a venue that has not published any prices yet.
      request.flush(null, { status: 204, statusText: 'No Content' });
    } else {
      request.flush({ id: 'p1', createdAt: '2026-09-19T00:00:00Z', bands });
    }
    fixture.detectChanges();
  }

  it('says when a venue has no prices yet', () => {
    render(null);

    expect(textOf(fixture, 'no-prices')).toBe(TRANSLATIONS.th['pricing.none']);
    expect(elementOf(fixture, 'price-list')).toBeNull();
  });

  it('reads the published prices back by day', () => {
    render(WEEKDAY_BANDS);

    expect(textOf(fixture, 'price-Monday')).toContain('6:00–18:00');
    expect(textOf(fixture, 'price-Monday')).toContain('300');
  });

  it('starts the editor from what is published', () => {
    render(WEEKDAY_BANDS);

    expect(elementOf(fixture, 'band-0')).not.toBeNull();
    expect(elementOf(fixture, 'band-1')).not.toBeNull();
    expect(elementOf(fixture, 'band-2')).toBeNull();
  });

  it('publishes the whole list, not just the row that changed', () => {
    render(WEEKDAY_BANDS);

    setInput(fixture, '[data-testid="band-1"] input[type="number"]', '350');
    submitForm(fixture);

    const request = httpMock.expectOne('/api/venues/v1/prices');
    const body = request.request.body as { bands: { bahtPerHour: number }[] };
    expect(body.bands).toHaveLength(2);
    expect(body.bands[0].bahtPerHour).toBe(200);
    expect(body.bands[1].bahtPerHour).toBe(350);

    request.flush({
      id: 'p2',
      createdAt: '2026-09-19T01:00:00Z',
      bands: [WEEKDAY_BANDS[0], { ...WEEKDAY_BANDS[1], bahtPerHour: 350 }],
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'price-Monday')).toContain('350');
  });

  it('adds and removes a band without touching the server', () => {
    render(WEEKDAY_BANDS);

    check(fixture, '[data-testid="add-band"]');
    expect(elementOf(fixture, 'band-2')).not.toBeNull();

    check(fixture, '[data-testid="remove-band-2"]');
    expect(elementOf(fixture, 'band-2')).toBeNull();
    httpMock.expectNone('/api/venues/v1/prices');
  });

  it('translates a refusal and keeps the rows as they were', () => {
    render(WEEKDAY_BANDS);

    submitForm(fixture);
    httpMock
      .expectOne('/api/venues/v1/prices')
      .flush({ code: 'pricing.hour_without_price' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(textOf(fixture, 'price-error')).toBe(
      TRANSLATIONS.th['error.pricing.hour_without_price'],
    );
    expect(elementOf(fixture, 'band-1')).not.toBeNull();
  });

  it('offers no editor to someone without the permission', () => {
    render(WEEKDAY_BANDS, false);

    expect(elementOf(fixture, 'band-0')).toBeNull();
    expect(elementOf(fixture, 'add-band')).toBeNull();
    // Reading them is still the point of the card.
    expect(textOf(fixture, 'price-Monday')).toContain('200');
  });

  it('reloads when the venue changes', () => {
    render(WEEKDAY_BANDS);

    fixture.componentRef.setInput('venueId', 'v2');
    fixture.detectChanges();

    httpMock
      .expectOne('/api/venues/v2/prices')
      .flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(textOf(fixture, 'no-prices')).toBe(TRANSLATIONS.th['pricing.none']);
  });
});
