import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { clickOn, elementOf, pageProviders, textOf } from '../../testing/dom';
import { TRANSLATIONS } from '../../testing/translations';
import { VenueStaff } from './venue-staff';

function type(root: { nativeElement: HTMLElement }, testId: string, value: string): void {
  const input = root.nativeElement.querySelector(`[data-testid="${testId}"]`) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

const OWNER = {
  userId: 'u0',
  email: 'demo@courtpaka.local',
  role: 'Owner',
  permissions: ['VerifySlip', 'ManageBookings', 'CloseCourt', 'ViewReports', 'ManageSettings'],
  refundLimitBaht: null,
};
const BOM = {
  userId: 'u1',
  email: '',
  name: 'บอม',
  phone: '0867777710',
  role: 'Staff',
  permissions: ['ManageBookings'],
  refundLimitBaht: 300,
};

describe('VenueStaff (thai-fit T1)', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [VenueStaff], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  function open(invitations: unknown[] = []) {
    const fixture = TestBed.createComponent(VenueStaff);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.componentRef.setInput('venueName', 'อารีย์');
    fixture.detectChanges();
    answer(invitations);
    fixture.detectChanges();
    return fixture;
  }

  function answer(invitations: unknown[] = []): void {
    httpMock.expectOne('/api/venues/v1/members').flush([OWNER, BOM]);
    httpMock.expectOne('/api/venues/v1/invitations').flush(invitations);
  }

  it('shows somebody with no address by name and phone, and changes what they may do', () => {
    const fixture = open();

    expect(textOf(fixture, 'staff-u1')).toContain('บอม');
    expect(textOf(fixture, 'staff-u1')).toContain('0867777710');
    // The owner's row has no boxes to untick: the owner has every permission, always.
    expect(elementOf(fixture, 'staff-remove-u0')).toBeNull();

    clickOn(fixture, 'staff-perm-u1-VerifySlip');
    const change = httpMock.expectOne('/api/venues/v1/members/u1/permissions');
    expect(change.request.body).toEqual({ permissions: ['ManageBookings', 'VerifySlip'] });
    change.flush(null);
    answer();
  });

  it('asks twice before taking somebody off the branch', () => {
    const fixture = open();

    clickOn(fixture, 'staff-remove-u1');
    fixture.detectChanges();
    httpMock.expectNone('/api/venues/v1/members/u1');
    expect(textOf(fixture, 'staff-remove-u1')).toBe(TRANSLATIONS.th['staff.removeSure']);

    clickOn(fixture, 'staff-remove-u1');
    httpMock.expectOne({ method: 'DELETE', url: '/api/venues/v1/members/u1' }).flush(null);
    answer();
  });

  it('makes a link for a name and a phone, and offers it to LINE', () => {
    const fixture = open();
    expect((elementOf(fixture, 'invite-create') as HTMLButtonElement).disabled).toBe(true);

    type(fixture, 'invite-name', 'ฝน');
    type(fixture, 'invite-phone', '095-444-4408');
    fixture.detectChanges();
    clickOn(fixture, 'invite-create');

    const invite = httpMock.expectOne('/api/venues/v1/invitations');
    expect(invite.request.body).toEqual({
      name: 'ฝน',
      phone: '095-444-4408',
      email: null,
      permissions: ['ManageBookings'],
    });
    const link = 'http://localhost/venue-invitation?invitationId=i9&token=abc';
    invite.flush({
      id: 'i9',
      email: null,
      name: 'ฝน',
      phone: '0954444408',
      permissions: ['ManageBookings'],
      expiresAt: '2026-10-17T10:00:00Z',
      link,
    });
    answer([
      {
        id: 'i9',
        email: null,
        name: 'ฝน',
        phone: '0954444408',
        permissions: ['ManageBookings'],
        expiresAt: '2026-10-17T10:00:00Z',
      },
    ]);
    fixture.detectChanges();

    expect(textOf(fixture, 'invite-link')).toBe(link);
    const line = (elementOf(fixture, 'invite-line') as HTMLAnchorElement).href;
    expect(line.startsWith('https://line.me/R/msg/text/?')).toBe(true);
    expect(decodeURIComponent(line)).toContain(link);
    expect(decodeURIComponent(line)).toContain('อารีย์');
    expect(textOf(fixture, 'invitation-i9')).toContain('ฝน');
  });

  it('takes a pending link back', () => {
    const fixture = open([
      {
        id: 'i9',
        email: null,
        name: 'ฝน',
        phone: null,
        permissions: ['ManageBookings'],
        expiresAt: '2026-10-17T10:00:00Z',
      },
    ]);

    clickOn(fixture, 'invitation-revoke-i9');
    httpMock.expectOne({ method: 'DELETE', url: '/api/venues/v1/invitations/i9' }).flush(null);
    answer();
  });
});
