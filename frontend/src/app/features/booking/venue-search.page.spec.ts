import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { elementOf, pageProviders, textOf } from '../../testing/dom';
import { VenueSearchPage } from './venue-search.page';

const SMASH = {
  id: 'v1',
  code: 'SBC',
  name: 'Smash Court',
  addressLine: '1 ถนนทดสอบ',
  district: 'บางรัก',
  province: 'กรุงเทพมหานคร',
};

describe('VenueSearchPage', () => {
  let fixture: ComponentFixture<VenueSearchPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    // The app is zoneless, so fakeAsync is unavailable; the runner's own timers drive the debounce.
    vi.useFakeTimers();
    await TestBed.configureTestingModule({
      imports: [VenueSearchPage],
      providers: pageProviders(),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
  });

  /** The search waits for a pause in typing, so the spec lets that pause elapse. */
  function render(venues: unknown[] = [SMASH]): void {
    fixture = TestBed.createComponent(VenueSearchPage);
    fixture.detectChanges();
    vi.advanceTimersByTime(300);

    // The page lists every venue before anyone types.
    httpMock.expectOne('/api/venues/search').flush(venues);
    fixture.detectChanges();
  }

  it('lists the venues before anything is typed', () => {
    render();

    expect(textOf(fixture, 'venue-results')).toContain('Smash Court');
    expect(textOf(fixture, 'venue-results')).toContain('บางรัก');
  });

  it('says so when nothing matches', () => {
    render([]);

    expect(textOf(fixture, 'no-venues')).toBe(TRANSLATIONS.th['search.none']);
    expect(elementOf(fixture, 'venue-results')).toBeNull();
  });

  it('links each result to its own grid', () => {
    render();

    expect(elementOf(fixture, 'venue-result')?.getAttribute('href')).toBe('/book/v1');
  });

  it('translates a failed search without emptying the page of meaning', () => {
    fixture = TestBed.createComponent(VenueSearchPage);
    fixture.detectChanges();
    vi.advanceTimersByTime(300);

    httpMock
      .expectOne('/api/venues/search')
      .flush(null, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(textOf(fixture, 'search-error')).toBe(TRANSLATIONS.th['error.unknown']);
  });
});
