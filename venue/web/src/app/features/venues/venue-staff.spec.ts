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
const FON = {
  userId: 'u1',
  email: '',
  name: 'ฝน',
  phone: '0951114408',
  role: 'Staff',
  permissions: ['ManageBookings'],
  refundLimitBaht: 0,
  usesPasscode: true,
  neverSignedIn: true,
};

describe('VenueStaff (thai-fit T1, passcode)', () => {
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ imports: [VenueStaff], providers: pageProviders() });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  function open() {
    const fixture = TestBed.createComponent(VenueStaff);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();
    answer();
    fixture.detectChanges();
    return fixture;
  }

  function answer(members: unknown[] = [OWNER, FON]): void {
    httpMock.expectOne('/api/venues/v1/members').flush(members);
  }

  it('lists staff by name and the phone they sign in with, and who has not signed in yet', () => {
    const fixture = open();

    expect(textOf(fixture, 'staff-u1')).toContain('ฝน');
    expect(textOf(fixture, 'staff-u1')).toContain('0951114408');
    expect(elementOf(fixture, 'staff-fresh-u1')).not.toBeNull();
    // The owner has every permission and no passcode to reset.
    expect(elementOf(fixture, 'staff-passcode-u0')).toBeNull();
    expect(elementOf(fixture, 'staff-remove-u0')).toBeNull();
  });

  it('adds somebody and shows the passcode once, in two groups of three', () => {
    const fixture = open();
    expect((elementOf(fixture, 'add-staff') as HTMLButtonElement).disabled).toBe(true);

    type(fixture, 'add-name', 'ฝน');
    type(fixture, 'add-phone', '095-111-4408');
    fixture.detectChanges();
    clickOn(fixture, 'add-staff');

    const added = httpMock.expectOne('/api/venues/v1/staff');
    expect(added.request.body).toEqual({
      name: 'ฝน',
      phone: '095-111-4408',
      permissions: ['ManageBookings'],
    });
    added.flush({ member: FON, passcode: '482913' });
    answer();
    fixture.detectChanges();

    expect(textOf(fixture, 'staff-passcode')).toBe('482 913');
    clickOn(fixture, 'staff-hide');
    fixture.detectChanges();
    expect(textOf(fixture, 'staff-passcode')).not.toContain('482');
  });

  it('sets a new passcode for somebody who lost theirs', () => {
    const fixture = open();

    clickOn(fixture, 'staff-passcode-u1');
    httpMock
      .expectOne({ method: 'POST', url: '/api/venues/v1/members/u1/passcode' })
      .flush({ member: FON, passcode: '135790' });
    answer();
    fixture.detectChanges();

    expect(textOf(fixture, 'staff-passcode')).toBe('135 790');
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
});
