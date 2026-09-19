import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { pageProviders, textOf } from '../../testing/dom';
import { VenueDetailPage } from './venue-detail.page';

const VENUE = { id: 'v1', code: 'SBC', name: 'Smash Court', status: 'Approved' };
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

function signedInAs(email: string): void {
  const auth = TestBed.inject(AuthService);
  const httpMock = TestBed.inject(HttpTestingController);
  auth.loadCurrentUser().subscribe();
  httpMock
    .expectOne('/api/auth/me')
    .flush({ id: 'x', email, emailConfirmed: true, language: 'th' });
}

describe('VenueDetailPage', () => {
  let fixture: ComponentFixture<VenueDetailPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [VenueDetailPage],
      providers: [
        ...pageProviders(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ venueId: 'v1' }) } },
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  function render(members: unknown[]): void {
    fixture = TestBed.createComponent(VenueDetailPage);
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1').flush(VENUE);
    httpMock.expectOne('/api/venues/v1/members').flush(members);
    fixture.detectChanges();
  }

  it('shows the venue and its team', () => {
    signedInAs(STAFF.email);
    render([OWNER, STAFF]);

    expect(textOf(fixture, 'venue-name')).toBe('Smash Court');
    expect(textOf(fixture, 'member-list')).toContain(OWNER.email);
    expect(textOf(fixture, 'member-list')).toContain(STAFF.email);
  });

  it('hides member management from staff', () => {
    signedInAs(STAFF.email);
    render([OWNER, STAFF]);

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('[data-testid="remove-u2"]')).toBeNull();
    expect(
      element.querySelector<HTMLInputElement>('[data-testid="permission-u2-ViewReports"]')
        ?.disabled,
    ).toBe(true);
    httpMock.expectNone('/api/venues/v1/invitations');
  });

  it('lets the owner grant a permission', () => {
    signedInAs(OWNER.email);
    render([OWNER, STAFF]);
    httpMock.expectOne('/api/venues/v1/invitations').flush([]);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    element.querySelector<HTMLInputElement>('[data-testid="permission-u2-ViewReports"]')!.click();
    fixture.detectChanges();

    const request = httpMock.expectOne('/api/venues/v1/members/u2/permissions');
    // VerifySlip + ManageBookings + CloseCourt + ViewReports = 1 + 2 + 4 + 8
    expect(request.request.body).toEqual({ permissions: 15 });
    request.flush(null, { status: 204, statusText: 'No Content' });

    httpMock.expectOne('/api/venues/v1').flush(VENUE);
    httpMock.expectOne('/api/venues/v1/members').flush([OWNER, STAFF]);
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1/invitations').flush([]);
  });

  it('invites a staff member with the permissions ticked', () => {
    signedInAs(OWNER.email);
    render([OWNER]);
    httpMock.expectOne('/api/venues/v1/invitations').flush([]);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    const email = element.querySelector<HTMLInputElement>('#invite-email')!;
    email.value = 'new@example.com';
    email.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();

    const request = httpMock.expectOne('/api/venues/v1/invitations');
    // The default ticks are VerifySlip + ManageBookings + CloseCourt = 1 + 2 + 4
    expect(request.request.body).toEqual({ email: 'new@example.com', permissions: 7 });
    request.flush({
      id: 'i1',
      email: 'new@example.com',
      permissions: [],
      expiresAt: '2026-10-01T00:00:00Z',
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'invitation-list')).toContain('new@example.com');
  });

  it('translates an API refusal', () => {
    signedInAs(OWNER.email);
    render([OWNER]);
    httpMock.expectOne('/api/venues/v1/invitations').flush([]);
    fixture.detectChanges();

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
