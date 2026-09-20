import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import {
  check,
  elementOf,
  isDisabled,
  isOn,
  pageProviders,
  signInAs,
  controlOf,
  textOf,
} from '../../testing/dom';
import { VenueDetailPage } from './venue-detail.page';

const OWNER = {
  userId: 'u1',
  email: 'owner@example.com',
  role: 'Owner',
  permissions: ['VerifySlip', 'ManageBookings', 'CloseCourt', 'ViewReports', 'ManageSettings'],
};
const STAFF = {
  userId: 'u2',
  email: 'staff@example.com',
  role: 'Staff',
  permissions: ['VerifySlip', 'ManageBookings', 'CloseCourt'],
};

function venueAs(role: 'Owner' | 'Staff') {
  return {
    id: 'v1',
    code: 'SBC',
    name: 'Smash Court',
    status: 'Approved',
    role,
    permissions: role === 'Owner' ? OWNER.permissions : STAFF.permissions,
    wantsSlipEmails: true,
  };
}

describe('VenueDetailPage', () => {
  let fixture: ComponentFixture<VenueDetailPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [VenueDetailPage],
      providers: pageProviders(),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  const NOTHING_WAITING = { slipsToCheck: 0, bookingsWithMoneyWaiting: 0 };

  function render(
    role: 'Owner' | 'Staff',
    members: unknown[],
    attention: object = NOTHING_WAITING,
  ): void {
    signInAs(role === 'Owner' ? OWNER.email : STAFF.email);
    fixture = TestBed.createComponent(VenueDetailPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1').flush(venueAs(role));
    httpMock.expectOne('/api/venues/v1/members').flush(members);
    httpMock.expectOne('/api/venues/v1/attention').flush(attention);
    fixture.detectChanges();
    if (role === 'Owner') {
      httpMock.expectOne('/api/venues/v1/invitations').flush([]);
      fixture.detectChanges();
    }
  }

  it('shows the venue and its team', () => {
    render('Staff', [OWNER, STAFF]);

    expect(textOf(fixture, 'venue-name')).toBe('Smash Court');
    expect(textOf(fixture, 'member-list')).toContain(OWNER.email);
    expect(textOf(fixture, 'member-list')).toContain(STAFF.email);
  });

  it('hides member management from staff', () => {
    render('Staff', [OWNER, STAFF]);

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="remove-u2"]')).toBeNull();
    expect(isDisabled(fixture, 'permission-u2-ViewReports')).toBe(true);
    // The invitation list is owner-only, so a staff member never asks for it.
    httpMock.expectNone('/api/venues/v1/invitations');
  });

  it('grants a permission by name and keeps the page as it is', () => {
    render('Owner', [OWNER, STAFF]);

    const element = fixture.nativeElement as HTMLElement;
    check(fixture, '[data-testid="permission-u2-ViewReports"]');

    const request = httpMock.expectOne('/api/venues/v1/members/u2/permissions');
    expect(request.request.body).toEqual({
      permissions: ['VerifySlip', 'ManageBookings', 'CloseCourt', 'ViewReports'],
    });
    request.flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    // The change is applied locally; nothing is refetched.
    httpMock.expectNone('/api/venues/v1');
    httpMock.expectNone('/api/venues/v1/members');
    expect(isOn(fixture, 'permission-u2-ViewReports')).toBe(true);
  });

  it('removes a member from the list without refetching', () => {
    render('Owner', [OWNER, STAFF]);

    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="remove-u2"]')!
      .click();
    fixture.detectChanges();

    httpMock
      .expectOne('/api/venues/v1/members/u2')
      .flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(textOf(fixture, 'member-list')).not.toContain(STAFF.email);
    httpMock.expectNone('/api/venues/v1/members');
  });

  it('invites a staff member with the permissions ticked', () => {
    render('Owner', [OWNER]);

    const element = fixture.nativeElement as HTMLElement;
    const email = element.querySelector<HTMLInputElement>('#invite-email')!;
    email.value = 'new@example.com';
    email.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    const request = httpMock.expectOne('/api/venues/v1/invitations');
    expect(request.request.body).toEqual({
      email: 'new@example.com',
      permissions: ['VerifySlip', 'ManageBookings', 'CloseCourt'],
    });
    request.flush({
      id: 'i1',
      email: 'new@example.com',
      permissions: ['VerifySlip'],
      expiresAt: '2026-10-01T00:00:00Z',
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'invitation-list')).toContain('new@example.com');
  });

  it('reloads when the route moves to another venue', () => {
    render('Owner', [OWNER, STAFF]);

    fixture.componentRef.setInput('venueId', 'v2');
    fixture.detectChanges();

    // The page must not keep showing the previous venue's data under the new id.
    httpMock
      .expectOne('/api/venues/v2')
      .flush({ ...venueAs('Owner'), id: 'v2', name: 'Second Court' });
    httpMock.expectOne('/api/venues/v2/members').flush([OWNER]);
    httpMock.expectOne('/api/venues/v2/attention').flush(NOTHING_WAITING);
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v2/invitations').flush([]);
    fixture.detectChanges();

    expect(textOf(fixture, 'venue-name')).toBe('Second Court');
    expect(textOf(fixture, 'member-list')).not.toContain(STAFF.email);
  });

  it('keeps the page when one permission change fails', () => {
    render('Owner', [OWNER, STAFF]);

    check(fixture, '[data-testid="permission-u2-ViewReports"]');

    httpMock
      .expectOne('/api/venues/v1/members/u2/permissions')
      .flush({ code: 'venue.not_approved' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(textOf(fixture, 'member-error')).toBe(TRANSLATIONS.th['error.venue.not_approved']);
    // The checkbox goes back to what the server actually holds.
    expect(isOn(fixture, 'permission-u2-ViewReports')).toBe(false);
    // The roster and the invite form are still there.
    expect(textOf(fixture, 'member-list')).toContain(STAFF.email);
    expect((fixture.nativeElement as HTMLElement).querySelector('#invite-email')).not.toBeNull();
  });

  it('does not let a second change start while one is in flight', () => {
    render('Owner', [OWNER, STAFF]);

    const element = fixture.nativeElement as HTMLElement;
    check(fixture, '[data-testid="permission-u2-ViewReports"]');
    const first = httpMock.expectOne('/api/venues/v1/members/u2/permissions');

    // Every checkbox is disabled until the first request finishes, so nothing can overwrite it.
    expect(isDisabled(fixture, 'permission-u2-ManageSettings')).toBe(true);

    first.flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();
    expect(isDisabled(fixture, 'permission-u2-ManageSettings')).toBe(false);
  });

  it('offers no write controls on a suspended venue', () => {
    signInAs(OWNER.email);
    fixture = TestBed.createComponent(VenueDetailPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1').flush({ ...venueAs('Owner'), status: 'Suspended' });
    httpMock.expectOne('/api/venues/v1/members').flush([OWNER, STAFF]);
    httpMock.expectOne('/api/venues/v1/attention').flush(NOTHING_WAITING);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('#invite-email')).toBeNull();
    expect(element.querySelector('[data-testid="remove-u2"]')).toBeNull();
    expect(isDisabled(fixture, 'permission-u2-ViewReports')).toBe(true);
  });

  it('replaces a pending invitation for the same address whatever the capitalisation', () => {
    render('Owner', [OWNER]);

    const element = fixture.nativeElement as HTMLElement;
    const email = element.querySelector<HTMLInputElement>('#invite-email')!;

    for (const address of ['Bob@example.com', 'bob@example.com']) {
      email.value = address;
      email.dispatchEvent(new Event('input'));
      fixture.detectChanges();
      element.querySelector('form')!.dispatchEvent(new Event('submit'));
      fixture.detectChanges();
      httpMock.expectOne('/api/venues/v1/invitations').flush({
        id: address,
        email: address,
        permissions: ['VerifySlip'],
        expiresAt: '2026-10-01T00:00:00Z',
      });
      fixture.detectChanges();
    }

    const listed = (fixture.nativeElement as HTMLElement).querySelectorAll(
      '[data-testid="invitation-list"] .entry',
    );
    expect(listed.length).toBe(1);
  });

  it('translates an API refusal', () => {
    render('Owner', [OWNER]);

    const element = fixture.nativeElement as HTMLElement;
    const email = element.querySelector<HTMLInputElement>('#invite-email')!;
    email.value = 'staff@example.com';
    email.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    httpMock
      .expectOne('/api/venues/v1/invitations')
      .flush({ code: 'venue.already_member' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(textOf(fixture, 'invite-error')).toBe(TRANSLATIONS.th['error.venue.already_member']);
  });

  it('puts the number of things waiting on the door it belongs to', () => {
    render('Owner', [OWNER], { slipsToCheck: 3, bookingsWithMoneyWaiting: 1 });

    expect(textOf(fixture, 'slips-waiting')).toContain('3');
    expect(textOf(fixture, 'money-waiting')).toContain('1');
  });

  it('says nothing where there is nothing waiting', () => {
    render('Owner', [OWNER]);

    expect(elementOf(fixture, 'slips-waiting')).toBeNull();
    expect(elementOf(fixture, 'money-waiting')).toBeNull();
  });

  it('sends the choice about slip mail as it is switched', () => {
    render('Owner', [OWNER]);

    controlOf(fixture, '[data-testid="slip-emails"]').click();
    fixture.detectChanges();

    const request = httpMock.expectOne('/api/venues/v1/notifications');
    expect(request.request.body).toEqual({ wantsSlipEmails: false });
    request.flush(null, { status: 204, statusText: 'No Content' });
  });

  it('puts the switch back when the server refuses', () => {
    render('Owner', [OWNER]);

    controlOf(fixture, '[data-testid="slip-emails"]').click();
    fixture.detectChanges();

    httpMock
      .expectOne('/api/venues/v1/notifications')
      .flush({ code: 'venue.not_member' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    // The switch must never say something the server does not hold.
    expect(isOn(fixture, 'slip-emails')).toBe(true);
    expect(elementOf(fixture, 'slip-emails-error')).not.toBeNull();
  });
});
