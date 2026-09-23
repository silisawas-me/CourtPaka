import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, DeferBlockState, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { TRANSLATIONS } from '../../testing/translations';
import { choose, clickOn, elementOf, pageProviders, signInAs, textOf } from '../../testing/dom';
import { PublicVenueService } from '../../core/venues/public-venue.service';
import { AvailabilityPage, REFRESH_EVERY_MS } from './availability.page';

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

  /** Opens the queue card, which the page fetches only when somebody presses for it. */
  async function openWaitlist(): Promise<void> {
    const blocks = await fixture.getDeferBlocks();
    await blocks[blocks.length - 1].render(DeferBlockState.Complete);
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

  it('takes a place in the queue for a day that had nothing on it', async () => {
    signInAs('player@example.com');
    render(day(), '2026-09-19');

    // The queue is not carried by every visit to the grid: pressing for it is what fetches it.
    expect(elementOf(fixture, 'waitlist')).toBeNull();
    await openWaitlist();

    choose(fixture, '[data-testid=wait-from]', '19:00');
    choose(fixture, '[data-testid=wait-until]', '20:00');
    choose(fixture, '[data-testid=wait-hours]', '1');
    clickOn(fixture, 'wait-for-it');

    const request = httpMock.expectOne('/api/waitlist');
    // The window is offered from the hours this venue actually sells that day, so a queue can
    // never ask for an hour the venue does not have.
    expect(request.request.body).toEqual({
      venueId: 'v1',
      date: '2026-09-19',
      fromHour: 19,
      untilHour: 20,
      hours: 1,
    });

    request.flush({
      id: 'w1',
      venueId: 'v1',
      venueName: 'DEV01',
      date: '2026-09-19',
      fromHour: 19,
      untilHour: 20,
      hours: 1,
      state: 'Waiting',
      askedAt: '2026-09-18T10:00:00Z',
    });
    fixture.detectChanges();

    // Once they are in it, the page says so rather than offering to put them in it again.
    expect(elementOf(fixture, 'waiting-now')).not.toBeNull();
    expect(elementOf(fixture, 'wait-for-it')).toBeNull();
  });

  it('asks somebody with no session to sign in before they can wait', async () => {
    render();
    await openWaitlist();

    expect(elementOf(fixture, 'wait-for-it')).toBeNull();
    expect(elementOf(fixture, 'sign-in-to-wait')).not.toBeNull();
  });

  it('says what a refused place in a queue was refused for', async () => {
    signInAs('player@example.com');
    render();
    await openWaitlist();

    clickOn(fixture, 'wait-for-it');
    httpMock
      .expectOne('/api/waitlist')
      .flush({ code: 'waitlist.already_waiting' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'wait-error')).toBe(TRANSLATIONS.th['error.waitlist.already_waiting']);
  });

  it('holds the hours it was given, and hands over to the page that pays for it', () => {
    signInAs('player@example.com');
    // The day is named in the URL, and that is the day the booking is for.
    render(day(), '2026-09-19');
    pickHour('c1', 18);
    pickHour('c1', 19);

    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

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
      slipUploadedAt: null,
    });
    fixture.detectChanges();

    expect(navigate).toHaveBeenCalledWith(['/bookings', 'b1']);
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
    // Started on a named day rather than the page's own default, so that neither move below can
    // land on the day it is already showing — which is what happens when a hardcoded date here
    // turns out to be today, and then the move fires no request at all.
    render(day({ date: '2026-09-21' }), '2026-09-21');

    // Two moves in quick succession: the first answer must not win by arriving last.
    fixture.componentRef.setInput('date', '2026-09-22');
    fixture.detectChanges();
    const first = expectRead();

    fixture.componentRef.setInput('date', '2026-09-23');
    fixture.detectChanges();
    const second = expectRead();

    // The superseded read is cancelled, so its answer can never arrive late and win.
    expect(first.cancelled).toBe(true);

    second.flush(day({ date: '2026-09-23' }));
    fixture.detectChanges();

    expect(textOf(fixture, 'grid-date')).toContain('23');
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
  describe('while the page is open (US-02)', () => {
    beforeEach(() => {
      vi.useFakeTimers();
    });

    afterEach(() => {
      vi.useRealTimers();
    });

    const taken = () =>
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
      });

    function expectRefresh() {
      return httpMock.expectOne(
        (request) =>
          request.url === '/api/venues/v1/availability' &&
          request.params.get('refresh') === 'true' &&
          request.params.get('date') === '2026-09-19',
      );
    }

    it('reads the day again every ten seconds, marked as a refresh, without a progress bar', () => {
      render(day(), '2026-09-19');

      vi.advanceTimersByTime(REFRESH_EVERY_MS - 1);
      httpMock.expectNone('/api/venues/v1/availability');

      vi.advanceTimersByTime(1);
      fixture.detectChanges();
      expect(elementOf(fixture, 'availability-grid')).not.toBeNull();
      expectRefresh().flush(taken());
      fixture.detectChanges();

      expect(elementOf(fixture, 'cell-c1-18')?.classList.contains('booked')).toBe(true);
    });

    it('keeps the grid it has when a refresh fails', () => {
      render(day(), '2026-09-19');

      vi.advanceTimersByTime(REFRESH_EVERY_MS);
      expectRefresh().flush(null, { status: 503, statusText: 'Unavailable' });
      fixture.detectChanges();

      expect(elementOf(fixture, 'availability-grid')).not.toBeNull();
      expect(elementOf(fixture, 'page-error')).toBeNull();
    });

    it('does not read while the tab is hidden, and reads at once when it comes back', () => {
      render(day(), '2026-09-19');
      const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden');

      vi.advanceTimersByTime(REFRESH_EVERY_MS * 3);
      httpMock.expectNone('/api/venues/v1/availability');

      visibility.mockReturnValue('visible');
      document.dispatchEvent(new Event('visibilitychange'));
      expectRefresh().flush(taken());
      fixture.detectChanges();

      expect(elementOf(fixture, 'cell-c1-18')?.classList.contains('booked')).toBe(true);
      visibility.mockRestore();
    });

    it('does not let a refresh of the day just left land on the day moved to', () => {
      render(day(), '2026-09-19');

      vi.advanceTimersByTime(REFRESH_EVERY_MS);
      const stale = expectRefresh();

      fixture.componentRef.setInput('date', '2026-09-20');
      fixture.detectChanges();
      expect(stale.cancelled).toBe(true);
      expectRead().flush(day({ date: '2026-09-20' }));
      fixture.detectChanges();

      expect(elementOf(fixture, 'cell-c1-18')?.classList.contains('booked')).toBe(false);
    });
  });
  it('draws a day that was asked for before it existed, without asking again', () => {
    // What the app does at boot for a grid link: the request goes out before this page is here.
    TestBed.inject(PublicVenueService).prefetch('v1', '2026-09-19');
    const early = expectRead();

    fixture = TestBed.createComponent(AvailabilityPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('date', '2026-09-19');
    fixture.detectChanges();

    early.flush(day());
    fixture.detectChanges();

    expect(textOf(fixture, 'venue-name')).toBe('Smash Court');
    httpMock.expectNone('/api/venues/v1/availability');
  });

  describe('the day picker (PRD 8: what a booker waits for)', () => {
    it('shows the day without the calendar, and fetches it when asked', async () => {
      render(day(), '2026-09-19');

      // The calendar is a third of what the page would weigh, and only somebody changing day
      // needs it — so what is on screen first is the day itself.
      expect(textOf(fixture, 'day-placeholder')).toContain('2569');
      expect(elementOf(fixture, 'day-picker')).toBeNull();

      const blocks = await fixture.getDeferBlocks();
      await blocks[0].render(DeferBlockState.Complete);
      fixture.detectChanges();

      expect(elementOf(fixture, 'day-picker')).not.toBeNull();
      expect(elementOf(fixture, 'day-placeholder')).toBeNull();
    });
  });
});
