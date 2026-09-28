import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { OwnerToday, VenueToday } from '../../core/venues/venue.service';
import { TRANSLATIONS } from '../../testing/translations';
import { elementOf, pageProviders, textOf } from '../../testing/dom';
import { VenuesPage } from './venues.page';

const venue: VenueToday = {
  venueId: 'v1',
  name: 'Smash Court',
  status: 'Approved',
  keptBaht: 1200,
  lastWeekKeptBaht: 1000,
  bookings: 4,
  todayBookings: 7,
  byKind: [
    { kind: 'WalkIn', count: 5 },
    { kind: 'Series', count: 2 },
  ],
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
};

// 2026-09-30 is a Wednesday.
const today: OwnerToday = {
  date: '2026-09-30',
  keptBaht: 1200,
  lastWeekKeptBaht: 1000,
  bookings: 4,
  todayBookings: 7,
  byKind: venue.byKind,
  dueNow: 1,
  venues: [venue],
};

describe('VenuesPage', () => {
  let fixture: ComponentFixture<VenuesPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [VenuesPage],
      providers: pageProviders(),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(VenuesPage);
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  /*
   * The owner app's first page is today across every branch and nothing else: the list of venues
   * and the way to apply for another are not on it (the branches are the bar above).
   */
  it('is today at every branch, without a list of venues or a way to apply', () => {
    httpMock.expectOne('/api/venues/mine/today').flush(today);
    fixture.detectChanges();

    expect(elementOf(fixture, 'all-venues-today')).not.toBeNull();
    expect(elementOf(fixture, 'venue-list')).toBeNull();
    expect(elementOf(fixture, 'apply-link')).toBeNull();
    httpMock.expectNone('/api/venues/mine');
  });

  it('leads with the three numbers the design does', () => {
    httpMock.expectOne('/api/venues/mine/today').flush(today);
    fixture.detectChanges();

    expect(textOf(fixture, 'overview-kept')).toBe('฿1,200');
    // Against the same weekday last week, named.
    expect(textOf(fixture, 'overview-change')).toBe(
      '+20% ' +
        TRANSLATIONS.th['overview.vsLastWeek'].replace(
          '{day}',
          TRANSLATIONS.th['overview.weekday.3'],
        ),
    );
    expect(textOf(fixture, 'overview-bookings')).toBe('7');
    expect(textOf(fixture, 'overview-kinds')).toBe(
      `${TRANSLATIONS.th['overview.kind.WalkIn']} 5 · ${TRANSLATIONS.th['overview.kind.Series']} 2`,
    );
    expect(textOf(fixture, 'overview-due')).toBe('1');
  });

  it('says nothing about last week when last week had nothing', () => {
    httpMock
      .expectOne('/api/venues/mine/today')
      .flush({ ...today, lastWeekKeptBaht: 0, byKind: [] });
    fixture.detectChanges();

    expect(elementOf(fixture, 'overview-change')).toBeNull();
    expect(textOf(fixture, 'overview-kinds')).toBe(TRANSLATIONS.th['overview.noBookings']);
  });

  it('shades each hour and flags what wants somebody now', () => {
    httpMock.expectOne('/api/venues/mine/today').flush(today);
    fixture.detectChanges();

    // What wants somebody is said per venue, under the grid.
    expect(elementOf(fixture, 'overview-late-v1')).not.toBeNull();
    expect(textOf(fixture, 'overview-shut-v1')).toContain('Court 2');
    // Each hour says its number, to whoever cannot see the shade; an hour not sold says nothing.
    const row = elementOf(fixture, 'overview-venue-v1');
    expect(row?.getAttribute('href')).toBe('/venues/v1/timeline');
    const hours = row!.querySelectorAll('.heat-hour-cell');
    expect(hours[0].textContent?.trim()).toBe('50%');
    expect(hours[1].classList).toContain('off');
  });

  // The whole day across the card, so every branch's evening is in view without walking to it.
  it('draws every hour the branches sell today', () => {
    const allDay = Array.from({ length: 18 }, (_, index) => ({
      hour: 6 + index,
      sellable: 4,
      booked: 1,
    }));
    httpMock
      .expectOne('/api/venues/mine/today')
      .flush({ ...today, venues: [{ ...venue, hours: allDay }] });
    fixture.detectChanges();

    const heads = [...fixture.nativeElement.querySelectorAll('.heat-hour')].map((one: Element) =>
      one.textContent?.trim(),
    );
    expect(heads).toHaveLength(18);
    expect(heads[0]).toBe('6:00');
    expect(heads[17]).toBe('23:00');
    expect(elementOf(fixture, 'heat-later')).toBeNull();
  });

  it('leaves out a venue still applying, which has nothing on sale to compare', () => {
    const applying: VenueToday = {
      ...venue,
      venueId: 'v2',
      name: 'Still applying',
      status: 'Pending',
      sellableHours: 0,
      hours: [],
    };
    httpMock.expectOne('/api/venues/mine/today').flush({ ...today, venues: [venue, applying] });
    fixture.detectChanges();

    expect(elementOf(fixture, 'overview-venue-v1')).not.toBeNull();
    expect(elementOf(fixture, 'overview-venue-v2')).toBeNull();
  });

  it('points to the branch bar for somebody with no venue whose reports they read', () => {
    httpMock
      .expectOne('/api/venues/mine/today')
      .flush({ ...today, keptBaht: 0, todayBookings: 0, byKind: [], dueNow: 0, venues: [] });
    fixture.detectChanges();

    expect(elementOf(fixture, 'all-venues-today')).toBeNull();
    expect(textOf(fixture, 'overview-pick')).toBe(TRANSLATIONS.th['overview.pickBranch']);
  });
});
