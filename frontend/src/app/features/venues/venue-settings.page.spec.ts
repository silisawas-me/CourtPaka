import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { pageProviders, signInAs, textOf } from '../../testing/dom';
import { VenueSettingsPage } from './venue-settings.page';

const OWNER_EMAIL = 'owner@example.com';

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

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  it('adds a court and keeps it in the list without refetching', () => {
    render();

    const name = element().querySelector<HTMLInputElement>('#court-name')!;
    name.value = 'Court 1';
    name.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    element().querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    const request = httpMock.expectOne('/api/venues/v1/courts');
    expect(request.request.body).toEqual({ name: 'Court 1' });
    request.flush({ id: 'c1', name: 'Court 1', position: 0, isActive: true });
    fixture.detectChanges();

    expect(textOf(fixture, 'court-list')).toContain('Court 1');
    httpMock.expectNone('/api/venues/v1/courts');
  });

  it('reports a duplicate court name without losing the page', () => {
    render();

    const name = element().querySelector<HTMLInputElement>('#court-name')!;
    name.value = 'Court 1';
    name.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    element().querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    httpMock
      .expectOne('/api/venues/v1/courts')
      .flush({ code: 'court.name_already_used' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'court-error')).toBe(TRANSLATIONS.th['error.court.name_already_used']);
    expect(element().querySelector('#court-name')).not.toBeNull();
  });

  it('puts the in-use box back when the change is refused', () => {
    render({}, [{ id: 'c1', name: 'Court 1', position: 0, isActive: true }]);

    const box = element().querySelector<HTMLInputElement>('[data-testid="court-active-c1"]')!;
    box.click();
    fixture.detectChanges();

    httpMock
      .expectOne('/api/venues/v1/courts/c1/status')
      .flush({ code: 'venue.not_approved' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(
      element().querySelector<HTMLInputElement>('[data-testid="court-active-c1"]')?.checked,
    ).toBe(true);
    expect(textOf(fixture, 'court-error')).toBe(TRANSLATIONS.th['error.venue.not_approved']);
  });

  it('shows the week in force and fills the form from it', () => {
    render({}, [], [{ id: 's1', effectiveFrom: '2026-09-01', inForce: true, days: week(7, 23) }]);

    expect(textOf(fixture, 'hours-in-force')).toContain('2026-09-01');
    expect(textOf(fixture, 'hours-Monday')).toContain('7:00');
    expect(element().querySelector<HTMLSelectElement>('[data-testid="opens-Monday"]')?.value).toBe(
      '7',
    );
    expect(element().querySelector<HTMLSelectElement>('[data-testid="closes-Monday"]')?.value).toBe(
      '23',
    );
  });

  it('sends every weekday, with a closed day as nulls', () => {
    render({}, [], [{ id: 's1', effectiveFrom: '2026-09-01', inForce: true, days: week() }]);

    const date = element().querySelector<HTMLInputElement>('#effective-from')!;
    date.value = '2026-10-01';
    date.dispatchEvent(new Event('input'));
    element().querySelector<HTMLInputElement>('[data-testid="open-Monday"]')!.click();
    fixture.detectChanges();

    const forms = element().querySelectorAll('form');
    forms[forms.length - 1].dispatchEvent(new Event('submit'));
    fixture.detectChanges();

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

  it('translates a refused week', () => {
    render({}, [], []);

    const date = element().querySelector<HTMLInputElement>('#effective-from')!;
    date.value = '2020-01-01';
    date.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    const forms = element().querySelectorAll('form');
    forms[forms.length - 1].dispatchEvent(new Event('submit'));
    fixture.detectChanges();

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
    render({ role: 'Staff', permissions: ['VerifySlip'] }, [
      { id: 'c1', name: 'Court 1', position: 0, isActive: true },
    ]);

    expect(element().querySelector('#court-name')).toBeNull();
    expect(element().querySelector('#effective-from')).toBeNull();
    expect(
      element().querySelector<HTMLInputElement>('[data-testid="court-active-c1"]')?.disabled,
    ).toBe(true);
    expect(textOf(fixture, 'read-only')).toBe(TRANSLATIONS.th['settings.readOnly']);
  });

  it('offers no controls on a venue that is not approved', () => {
    render({ status: 'Suspended' }, [{ id: 'c1', name: 'Court 1', position: 0, isActive: true }]);

    expect(element().querySelector('#court-name')).toBeNull();
    expect(element().querySelector('#effective-from')).toBeNull();
  });

  it('reloads when the route moves to another venue', () => {
    render({}, [{ id: 'c1', name: 'Court 1', position: 0, isActive: true }]);

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
