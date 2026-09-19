import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { pageProviders, signInAs, textOf } from '../../testing/dom';
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

  function render(role: 'Owner' | 'Staff', members: unknown[]): void {
    signInAs(role === 'Owner' ? OWNER.email : STAFF.email);
    fixture = TestBed.createComponent(VenueDetailPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1').flush(venueAs(role));
    httpMock.expectOne('/api/venues/v1/members').flush(members);
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
    expect(
      element.querySelector<HTMLInputElement>('[data-testid="permission-u2-ViewReports"]')
        ?.disabled,
    ).toBe(true);
    // The invitation list is owner-only, so a staff member never asks for it.
    httpMock.expectNone('/api/venues/v1/invitations');
  });

  it('grants a permission by name and keeps the page as it is', () => {
    render('Owner', [OWNER, STAFF]);

    const element = fixture.nativeElement as HTMLElement;
    element.querySelector<HTMLInputElement>('[data-testid="permission-u2-ViewReports"]')!.click();
    fixture.detectChanges();

    const request = httpMock.expectOne('/api/venues/v1/members/u2/permissions');
    expect(request.request.body).toEqual({
      permissions: ['VerifySlip', 'ManageBookings', 'CloseCourt', 'ViewReports'],
    });
    request.flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    // The change is applied locally; nothing is refetched.
    httpMock.expectNone('/api/venues/v1');
    httpMock.expectNone('/api/venues/v1/members');
    expect(
      element.querySelector<HTMLInputElement>('[data-testid="permission-u2-ViewReports"]')?.checked,
    ).toBe(true);
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
});
