import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import {
  check,
  elementOf,
  pageProviders,
  setInput,
  signInAs,
  submitForm,
  textOf,
} from '../../testing/dom';
import { VenueSettingsPage } from './venue-settings.page';

const OWNER_EMAIL = 'owner@example.com';
const COURT = { id: 'c1', name: 'Court 1', position: 0, isActive: true };

function venue(overrides: Record<string, unknown> = {}) {
  return {
    id: 'v1',
    code: 'SBC',
    name: 'Smash Court',
    status: 'Approved',
    role: 'Owner',
    permissions: ['VerifySlip', 'ManageBookings', 'CloseCourt', 'ViewReports', 'ManageSettings'],
    ...overrides,
  };
}

function week(opens: number | null = 6, closes: number | null = 22) {
  return ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'].map(
    (day) => ({ day, opensHour: opens, closesHour: closes }),
  );
}

describe('VenueSettingsPage', () => {
  let fixture: ComponentFixture<VenueSettingsPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [VenueSettingsPage],
      providers: pageProviders(),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  function render(
    overrides: Record<string, unknown> = {},
    courts: unknown[] = [],
    schedules: unknown[] = [],
  ): void {
    signInAs(OWNER_EMAIL);
    fixture = TestBed.createComponent(VenueSettingsPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();

    httpMock.expectOne('/api/venues/v1').flush(venue(overrides));
    httpMock.expectOne('/api/venues/v1/courts').flush(courts);
    httpMock.expectOne('/api/venues/v1/opening-hours').flush(schedules);
    fixture.detectChanges();
  }

  it('adds a court and keeps it in the list without refetching', () => {
    render();

    setInput(fixture, '#court-name', 'Court 1');
    submitForm(fixture);

    const request = httpMock.expectOne('/api/venues/v1/courts');
    expect(request.request.body).toEqual({ name: 'Court 1' });
    request.flush(COURT);
    fixture.detectChanges();

    expect(textOf(fixture, 'court-list')).toContain('Court 1');
    httpMock.expectNone('/api/venues/v1/courts');
  });

  it('reports a duplicate court name without losing the page', () => {
    render();

    setInput(fixture, '#court-name', 'Court 1');
    submitForm(fixture);

    httpMock
      .expectOne('/api/venues/v1/courts')
      .flush({ code: 'court.name_already_used' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'court-error')).toBe(TRANSLATIONS.th['error.court.name_already_used']);
    expect(elementOf(fixture, 'court-error')).not.toBeNull();
    expect((fixture.nativeElement as HTMLElement).querySelector('#court-name')).not.toBeNull();
  });

  it('renames a court, keeping the position it already had', () => {
    render({}, [{ ...COURT, position: 3 }]);

    check(fixture, '[data-testid="rename-c1"]');
    setInput(fixture, '[data-testid="rename-input-c1"]', 'Centre court');
    submitForm(fixture);

    const request = httpMock.expectOne('/api/venues/v1/courts/c1');
    expect(request.request.body).toEqual({ name: 'Centre court', position: 3 });
    request.flush({ ...COURT, name: 'Centre court', position: 3 });
    fixture.detectChanges();

    expect(textOf(fixture, 'court-list')).toContain('Centre court');
    expect(elementOf(fixture, 'rename-input-c1')).toBeNull();
  });

  it('keeps the rename open when the new name is taken', () => {
    render({}, [COURT]);

    check(fixture, '[data-testid="rename-c1"]');
    setInput(fixture, '[data-testid="rename-input-c1"]', 'Court 2');
    submitForm(fixture);

    httpMock
      .expectOne('/api/venues/v1/courts/c1')
      .flush({ code: 'court.name_already_used' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'court-error')).toBe(TRANSLATIONS.th['error.court.name_already_used']);
    expect(elementOf(fixture, 'rename-input-c1')).not.toBeNull();
  });

  it('puts the in-use box back when the change is refused', () => {
    render({}, [COURT]);

    check(fixture, '[data-testid="court-active-c1"]');

    httpMock
      .expectOne('/api/venues/v1/courts/c1/status')
      .flush({ code: 'venue.not_approved' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(elementOf<HTMLInputElement>(fixture, 'court-active-c1')?.checked).toBe(true);
    expect(textOf(fixture, 'court-error')).toBe(TRANSLATIONS.th['error.venue.not_approved']);
  });

  it('disables only the court being saved', () => {
    render({}, [COURT, { id: 'c2', name: 'Court 2', position: 1, isActive: true }]);

    check(fixture, '[data-testid="court-active-c1"]');
    const request = httpMock.expectOne('/api/venues/v1/courts/c1/status');

    expect(elementOf<HTMLInputElement>(fixture, 'court-active-c1')?.disabled).toBe(true);
    expect(elementOf<HTMLInputElement>(fixture, 'court-active-c2')?.disabled).toBe(false);

    request.flush({ ...COURT, isActive: false });
    fixture.detectChanges();
    expect(elementOf<HTMLInputElement>(fixture, 'court-active-c1')?.disabled).toBe(false);
  });

  it('shows the week in force and fills the form from it', () => {
    render({}, [], [{ id: 's1', effectiveFrom: '2026-09-01', inForce: true, days: week(7, 23) }]);

    expect(textOf(fixture, 'hours-in-force')).toContain('2026-09-01');
    expect(textOf(fixture, 'hours-Monday')).toContain('7:00');
    expect(elementOf<HTMLSelectElement>(fixture, 'opens-Monday')?.value).toBe('7');
    expect(elementOf<HTMLSelectElement>(fixture, 'closes-Monday')?.value).toBe('23');
  });

  it('sends every weekday, with a closed day as nulls', () => {
    render({}, [], [{ id: 's1', effectiveFrom: '2026-09-01', inForce: true, days: week() }]);

    setInput(fixture, '#effective-from', '2026-10-01');
    check(fixture, '[data-testid="open-Monday"]');
    submitForm(fixture, 'form:has(#effective-from)');

    const request = httpMock.expectOne('/api/venues/v1/opening-hours');
    const body = request.request.body as { effectiveFrom: string; days: unknown[] };
    expect(body.effectiveFrom).toBe('2026-10-01');
    expect(body.days).toHaveLength(7);
    expect(body.days[0]).toEqual({ day: 'Monday', opensHour: null, closesHour: null });
    expect(body.days[1]).toEqual({ day: 'Tuesday', opensHour: 6, closesHour: 22 });

    request.flush({ id: 's2', effectiveFrom: '2026-10-01', inForce: false, days: week() });
    fixture.detectChanges();

    expect(textOf(fixture, 'hours-upcoming')).toContain('2026-10-01');
  });

  it('shows the newer week for a date it already had', () => {
    render(
      {},
      [],
      [
        { id: 's1', effectiveFrom: '2026-09-01', inForce: true, days: week() },
        { id: 's2', effectiveFrom: '2026-10-01', inForce: false, days: week() },
      ],
    );

    setInput(fixture, '#effective-from', '2026-10-01');
    submitForm(fixture, 'form:has(#effective-from)');
    httpMock
      .expectOne('/api/venues/v1/opening-hours')
      .flush({ id: 's3', effectiveFrom: '2026-10-01', inForce: false, days: week(9, 21) });
    fixture.detectChanges();

    const upcoming = (fixture.nativeElement as HTMLElement).querySelectorAll(
      '[data-testid="hours-upcoming"] li',
    );
    expect(upcoming.length).toBe(1);
  });

  it('translates a refused week', () => {
    render({}, [], []);

    setInput(fixture, '#effective-from', '2020-01-01');
    submitForm(fixture, 'form:has(#effective-from)');

    httpMock
      .expectOne('/api/venues/v1/opening-hours')
      .flush(
        { code: 'court.effective_date_in_the_past' },
        { status: 400, statusText: 'Bad Request' },
      );
    fixture.detectChanges();

    expect(textOf(fixture, 'hours-error')).toBe(
      TRANSLATIONS.th['error.court.effective_date_in_the_past'],
    );
  });

  it('offers no controls to someone without the permission', () => {
    render({ role: 'Staff', permissions: ['VerifySlip'] }, [COURT]);

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('#court-name')).toBeNull();
    expect(element.querySelector('#effective-from')).toBeNull();
    expect(elementOf(fixture, 'rename-c1')).toBeNull();
    expect(elementOf<HTMLInputElement>(fixture, 'court-active-c1')?.disabled).toBe(true);
    expect(textOf(fixture, 'read-only')).toBe(TRANSLATIONS.th['settings.readOnly']);
  });

  it('offers no controls on a venue that is not approved', () => {
    render({ status: 'Suspended' }, [COURT]);

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('#court-name')).toBeNull();
    expect(element.querySelector('#effective-from')).toBeNull();
  });

  it('reloads when the route moves to another venue', () => {
    render({}, [COURT]);

    fixture.componentRef.setInput('venueId', 'v2');
    fixture.detectChanges();

    httpMock.expectOne('/api/venues/v2').flush(venue({ id: 'v2', name: 'Second Court' }));
    httpMock.expectOne('/api/venues/v2/courts').flush([]);
    httpMock.expectOne('/api/venues/v2/opening-hours').flush([]);
    fixture.detectChanges();

    expect(textOf(fixture, 'venue-name')).toBe('Second Court');
    expect(textOf(fixture, 'no-courts')).toBe(TRANSLATIONS.th['settings.courts.none']);
  });
});
