import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { elementOf, pageProviders, signInAs, textOf } from '../../testing/dom';
import { AvailabilityPage } from './availability.page';

const VENUE = {
  id: 'v1',
  code: 'SBC',
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

  /** The one request the page makes, and what it asked for. */
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

  it('says so when the venue is closed that day', () => {
    render(day({ opensHour: null, closesHour: null, courts: [] }));

    expect(textOf(fixture, 'closed-that-day')).toBe(TRANSLATIONS.th['availability.closed']);
    expect(elementOf(fixture, 'availability-grid')).toBeNull();
  });

  it('asks an anonymous visitor to sign in before booking', () => {
    render();

    expect(textOf(fixture, 'sign-in-to-book')).toBe(TRANSLATIONS.th['availability.signInToBook']);
    expect(elementOf(fixture, 'book')).toBeNull();
  });

  it('offers to book once there is a session', () => {
    signInAs('player@example.com');
    render();

    expect(elementOf(fixture, 'book')).not.toBeNull();
    expect(elementOf(fixture, 'sign-in-to-book')).toBeNull();
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
