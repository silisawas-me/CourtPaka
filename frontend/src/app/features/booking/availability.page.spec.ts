import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNativeDateAdapter } from '@angular/material/core';
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
    date: '2026-09-19',
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
      providers: [...pageProviders([{ path: 'login', children: [] }]), provideNativeDateAdapter()],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    fixture.destroy(); // Stops the ten-second refresh before the test ends.
    httpMock.verify();
  });

  function render(availability: object = day()): void {
    fixture = TestBed.createComponent(AvailabilityPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock.expectOne('/api/venues/v1/public').flush(VENUE);
    httpMock
      .expectOne((request) => request.url === '/api/venues/v1/availability')
      .flush(availability);
    fixture.detectChanges();
  }

  it('draws a cell for every court and hour, with the price', () => {
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

    httpMock.expectOne('/api/venues/v1/public').flush(VENUE);
    httpMock
      .expectOne((request) => request.url === '/api/venues/v1/availability')
      .flush(
        { code: 'availability.date_too_far_ahead' },
        { status: 400, statusText: 'Bad Request' },
      );
    fixture.detectChanges();

    expect(textOf(fixture, 'page-error')).toBe(
      TRANSLATIONS.th['error.availability.date_too_far_ahead'],
    );
  });

  it('reads another day when the picker moves', () => {
    render();

    fixture.componentInstance['pick'](new Date(2026, 8, 25));
    fixture.detectChanges();

    const request = httpMock.expectOne(
      (candidate) => candidate.url === '/api/venues/v1/availability',
    );
    expect(request.request.params.get('date')).toBe('2026-09-25');
    request.flush(day({ date: '2026-09-25' }));
    fixture.detectChanges();
  });
});
