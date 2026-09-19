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

    request.flush({ courtId: 'c1', activeToday: false, scheduled: [] });
    fixture.detectChanges();
    expect(elementOf<HTMLInputElement>(fixture, 'court-active-c1')?.disabled).toBe(false);
  });

  it('shows the week in force and fills the form from it', () => {
    render({}, [], [{ id: 's1', effectiveFrom: '2026-09-01', inForce: true, days: week(7, 23) }]);

    expect(textOf(fixture, 'hours-in-force')).toContain('2026-09-01');
    expect(textOf(fixture, 'hours-Monday')).toContain('7:00');
    const monday = fixture.componentInstance['hoursForm'].controls.days.controls.Monday;
    expect(monday.getRawValue()).toEqual({ open: true, opensHour: 7, closesHour: 23 });
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

  it('sends the hours as numbers after the user picks them', () => {
    render({}, [], [{ id: 's1', effectiveFrom: '2026-09-01', inForce: true, days: week() }]);

    const opens = elementOf<HTMLSelectElement>(fixture, 'opens-Tuesday')!;
    opens.selectedIndex = 9;
    opens.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    setInput(fixture, '#effective-from', '2026-10-01');
    submitForm(fixture, 'form:has(#effective-from)');

    const request = httpMock.expectOne('/api/venues/v1/opening-hours');
    const body = request.request.body as { days: { day: string; opensHour: unknown }[] };
    // A select bound with [value] would hand back the string "9" and the type would be a lie.
    expect(body.days[1]).toEqual({ day: 'Tuesday', opensHour: 9, closesHour: 22 });
    request.flush({ id: 's2', effectiveFrom: '2026-10-01', inForce: false, days: week() });
    fixture.detectChanges();
  });

  it('shows the week it just published for today instead of the one it replaced', () => {
    render({}, [], [{ id: 's1', effectiveFrom: '2026-09-01', inForce: true, days: week(6, 22) }]);

    setInput(fixture, '#effective-from', '2026-09-19');
    submitForm(fixture, 'form:has(#effective-from)');
    httpMock
      .expectOne('/api/venues/v1/opening-hours')
      .flush({ id: 's2', effectiveFrom: '2026-09-19', inForce: true, days: week(9, 21) });
    fixture.detectChanges();

    expect(textOf(fixture, 'hours-in-force')).toContain('2026-09-19');
    expect(textOf(fixture, 'hours-Monday')).toContain('9:00');
    expect(elementOf(fixture, 'hours-upcoming')).toBeNull();
  });

  it('keeps the weeks starting later in date order', () => {
    render({}, [], [{ id: 's1', effectiveFrom: '2026-09-01', inForce: true, days: week() }]);

    for (const [id, date] of [
      ['s2', '2026-11-01'],
      ['s3', '2026-10-01'],
    ]) {
      setInput(fixture, '#effective-from', date);
      submitForm(fixture, 'form:has(#effective-from)');
      httpMock
        .expectOne('/api/venues/v1/opening-hours')
        .flush({ id, effectiveFrom: date, inForce: false, days: week() });
      fixture.detectChanges();
    }

    const listed = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll(
        '[data-testid="hours-upcoming"] li strong',
      ),
    ].map((item) => item.textContent);
    expect(listed).toEqual(['2026-10-01', '2026-11-01']);
  });

  it('lets a second court be changed while the first is still saving', () => {
    render({}, [COURT, { id: 'c2', name: 'Court 2', position: 1, isActive: true }]);

    check(fixture, '[data-testid="court-active-c1"]');
    const first = httpMock.expectOne('/api/venues/v1/courts/c1/status');

    check(fixture, '[data-testid="court-active-c2"]');
    const second = httpMock.expectOne('/api/venues/v1/courts/c2/status');

    // The first answering must not unlock the second, whose request is still out.
    first.flush({ courtId: 'c1', activeToday: false, scheduled: [] });
    fixture.detectChanges();
    expect(elementOf<HTMLInputElement>(fixture, 'court-active-c2')?.disabled).toBe(true);

    second.flush({ courtId: 'c2', activeToday: false, scheduled: [] });
    fixture.detectChanges();
    expect(elementOf<HTMLInputElement>(fixture, 'court-active-c2')?.disabled).toBe(false);
  });

  it('says when a court is already booked to go out of use', () => {
    render({}, [COURT]);

    check(fixture, '[data-testid="court-active-c1"]');
    httpMock.expectOne('/api/venues/v1/courts/c1/status').flush({
      courtId: 'c1',
      activeToday: true,
      scheduled: [
        { active: false, effectiveFrom: '2026-10-01', changedAt: '2026-09-19T00:00:00Z' },
      ],
    });
    fixture.detectChanges();

    // What the server stored wins over what the click asked for.
    expect(elementOf<HTMLInputElement>(fixture, 'court-active-c1')?.checked).toBe(true);
    expect(textOf(fixture, 'scheduled-c1')).toContain('2026-10-01');
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
