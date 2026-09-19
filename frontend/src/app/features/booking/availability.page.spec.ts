import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { clickOn, elementOf, pageProviders, signInAs, textOf } from '../../testing/dom';
import { AvailabilityPage } from './availability.page';

const VENUE = {
  id: 'v1',
  name: 'Smash Court',
  addressLine: '1 ถนนทดสอบ',
  district: 'บางรัก',
  province: 'กรุงเทพมหานคร',
};

function day(overrides: Record<string, unknown> = {}) {
  return {
    venue: VENUE,
    date: '2026-09-19',
    lastBookableDate: '2026-10-19',
    opensHour: 18,
    closesHour: 20,
    courts: [
      {
        courtId: 'c1',
        name: 'Court 1',
        hours: [
          { hour: 18, status: 'Free', bahtPerHour: 300 },
          { hour: 19, status: 'Free', bahtPerHour: 300 },
        ],
      },
    ],
    ...overrides,
  };
}

describe('AvailabilityPage', () => {
  let fixture: ComponentFixture<AvailabilityPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [AvailabilityPage],
      providers: pageProviders([{ path: 'login', children: [] }]),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  /** The one request the page makes to draw a day. */
  function expectRead() {
    return httpMock.expectOne((request) => request.url === '/api/venues/v1/availability');
  }

  function render(availability: object = day(), date?: string): void {
    fixture = TestBed.createComponent(AvailabilityPage);
    fixture.componentRef.setInput('venueId', 'v1');
    if (date) {
      fixture.componentRef.setInput('date', date);
    }
    fixture.detectChanges();

    expectRead().flush(availability);
    fixture.detectChanges();
  }

  /** Picks an hour the way a booker does: by touching the cell. */
  function pickHour(courtId: string, hour: number): void {
    clickOn(fixture, `cell-${courtId}-${hour}`);
  }

  it('draws a cell for every court and hour, with the price, from one request', () => {
    render();

    expect(textOf(fixture, 'venue-name')).toBe('Smash Court');
    expect(textOf(fixture, 'cell-c1-18')).toContain('300');
    expect(textOf(fixture, 'cell-c1-19')).toContain('300');
    expect(elementOf(fixture, 'cell-c1-20')).toBeNull();
  });

  it('marks an hour the court cannot take', () => {
    render(
      day({
        courts: [
          {
            courtId: 'c1',
            name: 'Court 1',
            hours: [
              { hour: 18, status: 'Closed', bahtPerHour: null },
              { hour: 19, status: 'Free', bahtPerHour: 300 },
            ],
          },
        ],
      }),
    );

    expect(elementOf(fixture, 'cell-c1-18')?.classList.contains('closed')).toBe(true);
    expect(textOf(fixture, 'cell-c1-18')).toContain('—');
    expect(elementOf(fixture, 'cell-c1-19')?.classList.contains('free')).toBe(true);
  });

  it('shows an hour someone else holds as booked, and will not pick it', () => {
    render(
      day({
        courts: [
          {
            courtId: 'c1',
            name: 'Court 1',
            hours: [
              { hour: 18, status: 'Booked', bahtPerHour: 300 },
              { hour: 19, status: 'Free', bahtPerHour: 300 },
            ],
          },
        ],
      }),
    );

    const booked = elementOf(fixture, 'cell-c1-18');
    expect(booked?.classList.contains('booked')).toBe(true);
    // A booked cell is not a button, so there is nothing to press.
    expect(booked?.querySelector('button')).toBeNull();
    expect(elementOf(fixture, 'booking-summary')).toBeNull();
  });

  it('says so when the venue is closed that day', () => {
    render(day({ opensHour: null, closesHour: null, courts: [] }));

    expect(textOf(fixture, 'closed-that-day')).toBe(TRANSLATIONS.th['availability.closed']);
    expect(elementOf(fixture, 'availability-grid')).toBeNull();
  });

  it('sums the hours picked and lets one be taken back', () => {
    render();

    pickHour('c1', 18);
    pickHour('c1', 19);

    expect(fixture.nativeElement.querySelectorAll('[data-testid=summary-slot]')).toHaveLength(2);
    expect(textOf(fixture, 'summary-total')).toContain('600');
    expect(elementOf(fixture, 'cell-c1-18')?.classList.contains('picked')).toBe(true);

    // Touching a picked hour again drops it.
    pickHour('c1', 18);
    expect(fixture.nativeElement.querySelectorAll('[data-testid=summary-slot]')).toHaveLength(1);
    expect(textOf(fixture, 'summary-total')).toContain('300');
  });

  it('asks an anonymous visitor to sign in rather than offering to book', () => {
    render();
    pickHour('c1', 18);

    expect(textOf(fixture, 'sign-in-to-book')).toBe(TRANSLATIONS.th['availability.signInToBook']);
    expect(elementOf(fixture, 'book')).toBeNull();
  });

  it('holds the hours it was given, and shows what is held', () => {
    signInAs('player@example.com');
    // The day is named in the URL, and that is the day the booking is for.
    render(day(), '2026-09-19');
    pickHour('c1', 18);
    pickHour('c1', 19);

    clickOn(fixture, 'book');

    const request = httpMock.expectOne('/api/bookings');
    expect(request.request.body).toEqual({
      venueId: 'v1',
      slots: [
        { courtId: 'c1', date: '2026-09-19', hour: 18 },
        { courtId: 'c1', date: '2026-09-19', hour: 19 },
      ],
    });

    request.flush({
      id: 'b1',
      venueId: 'v1',
      venueName: 'Smash Court',
      status: 'Held',
      createdAt: '2026-09-19T11:00:00Z',
      holdExpiresAt: '2026-09-19T11:15:00Z',
      totalBaht: 600,
      slots: [
        { courtId: 'c1', courtName: 'Court 1', date: '2026-09-19', hour: 18, bahtPerHour: 300 },
        { courtId: 'c1', courtName: 'Court 1', date: '2026-09-19', hour: 19, bahtPerHour: 300 },
      ],
    });
    fixture.detectChanges();

    // The hours it just took are read again, so the grid stops offering them.
    expectRead().flush(
      day({
        courts: [
          {
            courtId: 'c1',
            name: 'Court 1',
            hours: [
              { hour: 18, status: 'Booked', bahtPerHour: 300 },
              { hour: 19, status: 'Booked', bahtPerHour: 300 },
            ],
          },
        ],
      }),
    );
    fixture.detectChanges();

    expect(textOf(fixture, 'held-total')).toContain('600');
    // The picks are spent, so the summary is gone and nothing can be booked twice.
    expect(elementOf(fixture, 'booking-summary')).toBeNull();
    expect(elementOf(fixture, 'cell-c1-18')?.querySelector('button')).toBeNull();
  });

  it('explains a refusal and re-reads the day, because the grid has moved on', () => {
    signInAs('player@example.com');
    render(day(), '2026-09-19');
    pickHour('c1', 18);

    clickOn(fixture, 'book');

    httpMock
      .expectOne('/api/bookings')
      .flush({ code: 'booking.slot_just_taken' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'booking-error')).toBe(TRANSLATIONS.th['error.booking.slot_just_taken']);

    // The day is read again so the hour that was taken now shows as taken.
    expectRead().flush(
      day({
        courts: [
          {
            courtId: 'c1',
            name: 'Court 1',
            hours: [
              { hour: 18, status: 'Booked', bahtPerHour: 300 },
              { hour: 19, status: 'Free', bahtPerHour: 300 },
            ],
          },
        ],
      }),
    );
    fixture.detectChanges();

    expect(elementOf(fixture, 'cell-c1-18')?.classList.contains('booked')).toBe(true);
    // The pick is gone with it, and the reason is still on screen rather than gone with the pick.
    expect(elementOf(fixture, 'booking-summary')).toBeNull();
    expect(textOf(fixture, 'booking-error')).toBe(TRANSLATIONS.th['error.booking.slot_just_taken']);
  });

  it('re-prices the summary from the grid it is looking at, not from what it picked', () => {
    render();
    pickHour('c1', 18);
    expect(textOf(fixture, 'summary-total')).toContain('300');

    // The venue republished its prices; the day is read again and the summary follows it.
    fixture.componentInstance['refresh'].update((attempt: number) => attempt + 1);
    fixture.detectChanges();
    expectRead().flush(
      day({
        courts: [
          {
            courtId: 'c1',
            name: 'Court 1',
            hours: [
              { hour: 18, status: 'Free', bahtPerHour: 350 },
              { hour: 19, status: 'Free', bahtPerHour: 350 },
            ],
          },
        ],
      }),
    );
    fixture.detectChanges();

    expect(textOf(fixture, 'summary-total')).toContain('350');
  });

  it('drops a picked hour that someone else took while it was picked', () => {
    render();
    pickHour('c1', 18);
    pickHour('c1', 19);
    expect(textOf(fixture, 'summary-total')).toContain('600');

    fixture.componentInstance['refresh'].update((attempt: number) => attempt + 1);
    fixture.detectChanges();
    expectRead().flush(
      day({
        courts: [
          {
            courtId: 'c1',
            name: 'Court 1',
            hours: [
              { hour: 18, status: 'Booked', bahtPerHour: 300 },
              { hour: 19, status: 'Free', bahtPerHour: 300 },
            ],
          },
        ],
      }),
    );
    fixture.detectChanges();

    // The hour that went is not in the summary, and what is left still adds up.
    expect(fixture.nativeElement.querySelectorAll('[data-testid=summary-slot]')).toHaveLength(1);
    expect(textOf(fixture, 'summary-total')).toContain('300');
    expect(elementOf(fixture, 'cell-c1-18')?.classList.contains('picked')).toBe(false);
  });

  it('forgets the hours picked when the day changes', () => {
    render();
    pickHour('c1', 18);
    expect(elementOf(fixture, 'booking-summary')).not.toBeNull();

    fixture.componentRef.setInput('date', '2026-09-25');
    fixture.detectChanges();
    expectRead().flush(day({ date: '2026-09-25' }));
    fixture.detectChanges();

    expect(elementOf(fixture, 'booking-summary')).toBeNull();
  });

  it('translates a refused date', () => {
    fixture = TestBed.createComponent(AvailabilityPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    expectRead().flush(
      { code: 'availability.date_too_far_ahead' },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(textOf(fixture, 'page-error')).toBe(
      TRANSLATIONS.th['error.availability.date_too_far_ahead'],
    );
  });

  it('reads the day named in the URL', () => {
    render(day({ date: '2026-09-25' }), '2026-09-25');

    expect(textOf(fixture, 'grid-date')).toContain('25');
  });

  it('keeps only the newest day when two reads overlap', () => {
    render();

    // Two moves in quick succession: the first answer must not win by arriving last.
    fixture.componentRef.setInput('date', '2026-09-21');
    fixture.detectChanges();
    const first = expectRead();

    fixture.componentRef.setInput('date', '2026-09-22');
    fixture.detectChanges();
    const second = expectRead();

    // The superseded read is cancelled, so its answer can never arrive late and win.
    expect(first.cancelled).toBe(true);

    second.flush(day({ date: '2026-09-22' }));
    fixture.detectChanges();

    expect(textOf(fixture, 'grid-date')).toContain('22');
  });

  it('drops the grid it was showing when the next day cannot be read', () => {
    render();

    fixture.componentRef.setInput('date', '2030-01-01');
    fixture.detectChanges();
    expectRead().flush(
      { code: 'availability.date_too_far_ahead' },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(elementOf(fixture, 'availability-grid')).toBeNull();
    expect(elementOf(fixture, 'grid-date')).toBeNull();
  });

  it('navigates when the picker moves, so the day on screen is the day in the URL', () => {
    render();

    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    fixture.componentInstance['pick'](new Date(2026, 8, 25));

    expect(navigate).toHaveBeenCalledWith([], {
      queryParams: { date: '2026-09-25' },
      queryParamsHandling: 'merge',
    });
  });
});
