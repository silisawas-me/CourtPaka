import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { gridInTheAddress } from '../../app.config';
import { pageProviders } from '../../testing/dom';
import { plainDate, venueToday } from '../i18n/plain-date';
import { Availability, PublicVenueService } from './public-venue.service';

const DAY = { date: '2026-09-23', courts: [] } as unknown as Availability;

describe('PublicVenueService', () => {
  let venues: PublicVenueService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: pageProviders() });
    venues = TestBed.inject(PublicVenueService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  /**
   * The whole point of asking early: the page that draws the day is downloaded while the answer
   * is already on its way, and it reads that answer rather than sending a second request.
   */
  it('hands a day asked for early to whoever draws it, without asking again', () => {
    venues.prefetch('v1', '2026-09-23');
    const early = httpMock.expectOne((request) => request.url === '/api/venues/v1/availability');

    let drawn: Availability | null = null;
    venues.availability('v1', '2026-09-23').subscribe((day) => (drawn = day));
    early.flush(DAY);

    expect(drawn).toEqual(DAY);
    httpMock.expectNone('/api/venues/v1/availability');
  });

  it('asks the server again for every day after that one', () => {
    venues.prefetch('v1', '2026-09-23');
    httpMock.expectOne((request) => request.url === '/api/venues/v1/availability').flush(DAY);
    venues.availability('v1', '2026-09-23').subscribe();

    // The grid re-reads its day every ten seconds; an answer from boot is not that day any more.
    venues.availability('v1', '2026-09-23').subscribe();
    httpMock.expectOne((request) => request.url === '/api/venues/v1/availability').flush(DAY);
  });

  it('never answers a refresh from what was asked for early', () => {
    venues.prefetch('v1', '2026-09-23');
    const early = httpMock.expectOne((request) => request.url === '/api/venues/v1/availability');

    venues.availability('v1', '2026-09-23', true).subscribe();

    const refresh = httpMock.expectOne(
      (request) =>
        request.url === '/api/venues/v1/availability' && request.params.get('refresh') === 'true',
    );
    refresh.flush(DAY);
    early.flush(DAY);
  });

  /**
   * An answer that failed on the way is not an answer to hand on: whoever draws the day asks
   * again and shows whatever comes back, rather than inheriting a failure from boot.
   */
  it('asks again when the early answer never arrived', () => {
    venues.prefetch('v1', '2026-09-23');
    httpMock
      .expectOne((request) => request.url === '/api/venues/v1/availability')
      .flush(null, { status: 503, statusText: 'Unavailable' });

    let drawn: Availability | null = null;
    venues.availability('v1', '2026-09-23').subscribe((day) => (drawn = day));

    httpMock.expectOne((request) => request.url === '/api/venues/v1/availability').flush(DAY);
    expect(drawn).toEqual(DAY);
  });
});

describe('gridInTheAddress', () => {
  it('reads the venue and the day a grid link names', () => {
    expect(gridInTheAddress('/book/v1', '?date=2026-09-25')).toEqual({
      venueId: 'v1',
      date: '2026-09-25',
    });
  });

  it('falls back to today at the venue, which is what the page would ask for', () => {
    expect(gridInTheAddress('/book/v1', '')).toEqual({
      venueId: 'v1',
      date: plainDate(venueToday()),
    });
  });

  it('is not fooled by the pages next to it', () => {
    expect(gridInTheAddress('/book', '')).toBeNull();
    expect(gridInTheAddress('/bookings', '')).toBeNull();
    expect(gridInTheAddress('/bookings/b1', '')).toBeNull();
    expect(gridInTheAddress('/venues/v1/dashboard', '')).toBeNull();
  });
});
