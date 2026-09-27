import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../testing/translations';
import { elementOf, pageProviders, textOf } from '../../testing/dom';
import { VenuesPage } from './venues.page';

describe('VenuesPage', () => {
  let fixture: ComponentFixture<VenuesPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [VenuesPage],
      providers: pageProviders([{ path: 'venues/apply', children: [] }]),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(VenuesPage);
    fixture.detectChanges();
  });

  afterEach(() => {
    // Today across every venue is asked for alongside the list; most tests are not about it.
    for (const asked of httpMock.match('/api/venues/mine/today')) {
      asked.flush({ date: '2026-09-28', keptBaht: 0, bookings: 0, dueNow: 0, venues: [] });
    }
    httpMock.verify();
  });

  it('says so when the account runs no venues yet', () => {
    httpMock.expectOne('/api/venues/mine').flush([]);
    fixture.detectChanges();

    expect(textOf(fixture, 'no-venues')).toBe(TRANSLATIONS.th['venues.none']);
  });

  it('lists the venues the account belongs to', () => {
    httpMock
      .expectOne('/api/venues/mine')
      .flush([{ id: 'v1', code: 'SBC', name: 'Smash Court', status: 'Approved' }]);
    fixture.detectChanges();

    expect(textOf(fixture, 'venue-list')).toContain('Smash Court');
  });

  it('sends people to the application rather than carrying the form itself', () => {
    httpMock.expectOne('/api/venues/mine').flush([]);
    fixture.detectChanges();

    // Applying grew a tax identity and a bank account, and moved to its own page.
    expect(elementOf<HTMLAnchorElement>(fixture, 'apply-link')?.getAttribute('href')).toBe(
      '/venues/apply',
    );
  });

  /*
   * badPaka 2c: today across every venue, above the list — by each venue dashboard's own
   * numbers, which the server reads; the page only lays them out.
   */
  it('shows today at every venue, and flags what wants somebody now', () => {
    httpMock.expectOne('/api/venues/mine').flush([]);
    httpMock.expectOne('/api/venues/mine/today').flush({
      date: '2026-09-28',
      keptBaht: 1200,
      bookings: 4,
      dueNow: 1,
      venues: [
        {
          venueId: 'v1',
          name: 'Smash Court',
          status: 'Approved',
          keptBaht: 1200,
          bookings: 4,
          sellableHours: 8,
          bookedHours: 2,
          usedPercent: 25,
          hours: [
            { hour: 18, sellable: 4, booked: 2 },
            { hour: 19, sellable: 0, booked: 0 },
          ],
          dueNow: 1,
          pastGrace: 1,
          shutNow: ['Court 2'],
        },
      ],
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'overview-kept')).toBe('1,200');
    expect(textOf(fixture, 'overview-due')).toBe('1');
    const venue = elementOf(fixture, 'overview-venue-v1');
    // Past the grace outranks merely due: it is the one the venue has to decide about.
    expect(elementOf(fixture, 'overview-late-v1')).not.toBeNull();
    expect(elementOf(fixture, 'overview-due-v1')).toBeNull();
    expect(textOf(fixture, 'overview-shut-v1')).toContain('Court 2');
    // Each hour says its number to whoever cannot see the shade; an hour not on sale says so.
    const hours = venue!.querySelectorAll('.heat-hour');
    expect(hours[0].getAttribute('aria-label')).toContain('50%');
    expect(hours[1].classList).toContain('off');
  });

  it('draws nothing for somebody with no venue whose reports they read', () => {
    httpMock.expectOne('/api/venues/mine').flush([]);
    httpMock
      .expectOne('/api/venues/mine/today')
      .flush({ date: '2026-09-28', keptBaht: 0, bookings: 0, dueNow: 0, venues: [] });
    fixture.detectChanges();

    expect(elementOf(fixture, 'all-venues-today')).toBeNull();
  });
});
