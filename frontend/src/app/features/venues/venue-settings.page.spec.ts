import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNativeDateAdapter } from '@angular/material/core';
import { plainDate, venueToday } from '../../core/i18n/plain-date';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { DATE_LOCALES } from '../../core/i18n/locales';
import { TRANSLATIONS } from '../../testing/translations';
import {
  check,
  isDisabled,
  isOn,
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
    depositPercent: 100,
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
      // The page provides its own adapter; the spec says so rather than the helper providing it
      // for every page, which would hide a page that forgot to.
      providers: [...pageProviders(), provideNativeDateAdapter()],
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
    settleChildren('v1');
  }

  /** The prices and policy cards load themselves; their own specs cover what they then do. */
  function settleChildren(venueId: string): void {
    httpMock
      .expectOne(`/api/venues/${venueId}/prices`)
      .flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne(`/api/venues/${venueId}/cancellation-policy`).flush({
      id: 'c0',
      createdAt: '2026-09-19T00:00:00Z',
      tiers: [{ hoursBefore: 24, refundPercent: 100 }],
    });
    fixture.detectChanges();
  }

  /** The picker works in Dates; the page turns one into the plain date the API takes. */
  function setDate(isoDate: string): void {
    const [year, month, day] = isoDate.split('-').map(Number);
    fixture.componentInstance['hoursForm'].controls.effectiveFrom.setValue(
      new Date(year, month - 1, day),
    );
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

    expect(isOn(fixture, 'court-active-c1')).toBe(true);
    expect(textOf(fixture, 'court-error')).toBe(TRANSLATIONS.th['error.venue.not_approved']);
  });

  it('disables only the court being saved', () => {
    render({}, [COURT, { id: 'c2', name: 'Court 2', position: 1, isActive: true }]);

    check(fixture, '[data-testid="court-active-c1"]');
    const request = httpMock.expectOne('/api/venues/v1/courts/c1/status');

    expect(isDisabled(fixture, 'court-active-c1')).toBe(true);
    expect(isDisabled(fixture, 'court-active-c2')).toBe(false);

    request.flush({ courtId: 'c1', activeToday: false, scheduled: [] });
    fixture.detectChanges();
    expect(isDisabled(fixture, 'court-active-c1')).toBe(false);
  });

  it('shows the week in force and fills the form from it', () => {
    render({}, [], [{ id: 's1', effectiveFrom: '2026-09-01', inForce: true, days: week(7, 23) }]);

    expect(textOf(fixture, 'hours-in-force')).toContain('2569');
    expect(textOf(fixture, 'hours-Monday')).toContain('7:00');
    const monday = fixture.componentInstance['hoursForm'].controls.days.controls.Monday;
    expect(monday.getRawValue()).toEqual({ open: true, opensHour: 7, closesHour: 23 });
  });

  it('sends every weekday, with a closed day as nulls', () => {
    render({}, [], [{ id: 's1', effectiveFrom: '2026-09-01', inForce: true, days: week() }]);

    setDate('2026-10-01');
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

    expect(textOf(fixture, 'hours-upcoming')).toContain('1 ต.ค. 2569');
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

    setDate('2026-10-01');
    submitForm(fixture, 'form:has(#effective-from)');
    httpMock
      .expectOne('/api/venues/v1/opening-hours')
      .flush({ id: 's3', effectiveFrom: '2026-10-01', inForce: false, days: week(9, 21) });
    fixture.detectChanges();

    const upcoming = (fixture.nativeElement as HTMLElement).querySelectorAll(
      '[data-testid="hours-upcoming"] .entry',
    );
    expect(upcoming.length).toBe(1);
  });

  it('translates a refusal the form could not have known about', () => {
    render({}, [], []);

    // The picker will not offer a past date, but the day can turn over between picking and saving,
    // so the server's refusal still has to reach the page in the reader's language.
    setDate('2026-10-01');
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

  it('will not send a date the server would refuse, and says why', () => {
    render({}, [], []);

    setDate('2020-01-01');
    submitForm(fixture, 'form:has(#effective-from)');

    // Nothing leaves the page: the date is before the minimum the picker allows.
    httpMock.expectNone('/api/venues/v1/opening-hours');
    // And the field says that, rather than claiming a filled-in field is empty.
    expect(textOf(fixture, 'effective-from-error')).toBe(TRANSLATIONS.th['common.dateTooEarly']);
  });

  it('sends the hours as numbers after the user picks them', () => {
    render({}, [], [{ id: 's1', effectiveFrom: '2026-09-01', inForce: true, days: week() }]);

    // A Material select opens an overlay; setting the control is what picking an option does.
    fixture.componentInstance[
      'hoursForm'
    ].controls.days.controls.Tuesday.controls.opensHour.setValue(9);
    fixture.detectChanges();

    setDate('2026-10-01');
    submitForm(fixture, 'form:has(#effective-from)');

    const request = httpMock.expectOne('/api/venues/v1/opening-hours');
    const body = request.request.body as { days: { day: string; opensHour: unknown }[] };
    // A select bound with [value] on a plain <option> would hand back "9" and the type would lie.
    expect(body.days[1]).toEqual({ day: 'Tuesday', opensHour: 9, closesHour: 22 });
    request.flush({ id: 's2', effectiveFrom: '2026-10-01', inForce: false, days: week() });
    fixture.detectChanges();
  });

  it('shows the week it just published for today instead of the one it replaced', () => {
    render({}, [], [{ id: 's1', effectiveFrom: '2026-09-01', inForce: true, days: week(6, 22) }]);

    // A week starting today. The picker refuses a past date, so this cannot be a fixed one.
    const today = plainDate(venueToday());
    setDate(today);
    submitForm(fixture, 'form:has(#effective-from)');
    httpMock
      .expectOne('/api/venues/v1/opening-hours')
      .flush({ id: 's2', effectiveFrom: today, inForce: true, days: week(9, 21) });
    fixture.detectChanges();

    expect(textOf(fixture, 'hours-in-force')).toContain(
      new AppDatePipe().transform(today, DATE_LOCALES.th),
    );
    expect(textOf(fixture, 'hours-Monday')).toContain('9:00');
    expect(elementOf(fixture, 'hours-upcoming')).toBeNull();
  });

  it('keeps the weeks starting later in date order', () => {
    render({}, [], [{ id: 's1', effectiveFrom: '2026-09-01', inForce: true, days: week() }]);

    for (const [id, date] of [
      ['s2', '2026-11-01'],
      ['s3', '2026-10-01'],
    ]) {
      setDate(date);
      submitForm(fixture, 'form:has(#effective-from)');
      httpMock
        .expectOne('/api/venues/v1/opening-hours')
        .flush({ id, effectiveFrom: date, inForce: false, days: week() });
      fixture.detectChanges();
    }

    const listed = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll(
        '[data-testid="hours-upcoming"] .entry-name',
      ),
    ].map((item) => item.textContent);
    expect(listed).toEqual(['1 ต.ค. 2569', '1 พ.ย. 2569']);
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
    expect(isDisabled(fixture, 'court-active-c2')).toBe(true);

    second.flush({ courtId: 'c2', activeToday: false, scheduled: [] });
    fixture.detectChanges();
    expect(isDisabled(fixture, 'court-active-c2')).toBe(false);
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
    expect(isOn(fixture, 'court-active-c1')).toBe(true);
    expect(textOf(fixture, 'scheduled-c1')).toContain('1 ต.ค. 2569');
  });

  it('offers no controls to someone without the permission', () => {
    render({ role: 'Staff', permissions: ['VerifySlip'] }, [COURT]);

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('#court-name')).toBeNull();
    expect(element.querySelector('#effective-from')).toBeNull();
    expect(elementOf(fixture, 'rename-c1')).toBeNull();
    expect(isDisabled(fixture, 'court-active-c1')).toBe(true);
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
    settleChildren('v2');

    expect(textOf(fixture, 'venue-name')).toBe('Second Court');
    expect(textOf(fixture, 'no-courts')).toBe(TRANSLATIONS.th['settings.courts.none']);
  });

  /**
   * What a venue asks for before it holds hours is its own call (PRD US-28), and the page sends
   * what was typed rather than judging it: the range is the server's rule, and a second copy of
   * it here is a copy that can disagree with the refusal (US-23).
   */
  it('sends the share the venue asks for up front', () => {
    render();

    setInput(fixture, '[data-testid="deposit-percent"]', '50');
    (elementOf(fixture, 'deposit-percent') as HTMLInputElement).dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const request = httpMock.expectOne('/api/venues/v1/deposit');
    expect(request.request.body).toEqual({ percent: 50 });
    request.flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(elementOf(fixture, 'deposit-saved')).not.toBeNull();
  });

  it('translates a share the server will not take', () => {
    render();

    setInput(fixture, '[data-testid="deposit-percent"]', '5');
    (elementOf(fixture, 'deposit-percent') as HTMLInputElement).dispatchEvent(new Event('change'));
    fixture.detectChanges();

    httpMock
      .expectOne('/api/venues/v1/deposit')
      .flush({ code: 'venue.invalid_deposit' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(textOf(fixture, 'deposit-error')).toBe(TRANSLATIONS.th['error.venue.invalid_deposit']);
    // And the field goes back to what the venue is actually on, so the refused number is not
    // left on screen reading as the one in force.
    expect((elementOf(fixture, 'deposit-percent') as HTMLInputElement).value).toBe('100');
  });
});
