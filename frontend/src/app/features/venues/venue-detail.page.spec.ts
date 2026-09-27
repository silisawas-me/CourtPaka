import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../testing/translations';
import {
  check,
  controlOf,
  elementOf,
  isDisabled,
  isOn,
  pageProviders,
  signInAs,
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

  function render(role: 'Owner' | 'Staff', members: unknown[]): void {
    signInAs(role === 'Owner' ? OWNER.email : STAFF.email);
    fixture = TestBed.createComponent(VenueDetailPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1').flush(venueAs(role));
    httpMock.expectOne('/api/venues/v1/members').flush(members);
    fixture.detectChanges();

    // What the venue owes the platform is read with the page (PRD US-21). Most venues have
    // nothing, and then the card is not drawn at all.
    httpMock.expectOne('/api/venues/v1/commission').flush({ account: null, invoices: [] });
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
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v2/commission').flush({ account: null, invoices: [] });
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
    fixture.detectChanges();

    // A suspended venue still owes what it owed, so it is still asked for (PRD US-20, US-21).
    httpMock.expectOne('/api/venues/v1/commission').flush({ account: null, invoices: [] });
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

  // What is waiting is drawn beside the doors by the venue's shell, on every one of its pages —
  // `app.spec.ts` is where that is checked.

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

  /**
   * Holding the permission is being given the work, not being trusted with any amount of the
   * venue's money (PRD US-18). The owner says the number, on the row that carries the permission
   * it qualifies.
   */
  it('sends what the owner trusts a member with, keeping the permissions they already had', () => {
    render('Owner', [OWNER, STAFF]);

    // The test id is on the input itself, the way every other number field on this page does it.
    const input = controlOf(fixture, '[data-testid="refund-limit-u2"]') as HTMLInputElement;
    input.value = '500';
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const sent = httpMock.expectOne('/api/venues/v1/members/u2/permissions');
    expect(sent.request.body).toEqual({
      permissions: ['VerifySlip', 'ManageBookings', 'CloseCourt'],
      refundLimitBaht: 500,
    });
    sent.flush(null);
    fixture.detectChanges();

    expect(input.value).toBe('500');
  });

  /**
   * Ticking a permission must not quietly take away what somebody was trusted with, so a request
   * that is not about the limit says nothing about it (PRD US-18).
   */
  it('says nothing about the limit when only a permission changed', () => {
    render('Owner', [OWNER, STAFF]);

    check(fixture, '[data-testid="permission-u2-ViewReports"]');

    const sent = httpMock.expectOne('/api/venues/v1/members/u2/permissions');
    expect(sent.request.body).not.toHaveProperty('refundLimitBaht');
    sent.flush(null);
  });

  /** The owner has no ceiling, so there is no number to set against their own row. */
  it('offers no limit against the owner', () => {
    render('Owner', [OWNER, STAFF]);

    expect(elementOf(fixture, 'refund-limit-u1')).toBeNull();
  });

  /**
   * What this venue owes the platform (PRD US-21). On the page the owner already opens: a venue
   * should not have to go looking to find out it is late.
   */
  it('draws no commission card for a venue that has never been billed', () => {
    render('Owner', [OWNER]);

    expect(elementOf(fixture, 'commission')).toBeNull();
  });

  it('says what is owed, where to send it, and that it is late', () => {
    renderWithCommission({
      account: { promptPayId: '0899999999', accountName: 'CourtPaka' },
      invoices: [invoice({ overdue: true })],
    });

    expect(textOf(fixture, 'pay-to')).toContain('0899999999');
    expect(textOf(fixture, 'invoice-i1')).toContain('4,000');

    // Late is shown beside the status, not instead of it (PRD US-21).
    expect(textOf(fixture, 'overdue-i1')).toBe(TRANSLATIONS.th['commission.overdue']);
    expect(textOf(fixture, 'invoice-i1')).toContain(TRANSLATIONS.th['commission.status.Issued']);
  });

  it('says so when the platform has not given an account to pay into', () => {
    renderWithCommission({ account: null, invoices: [invoice()] });

    expect(textOf(fixture, 'no-account')).toBe(TRANSLATIONS.th['commission.noAccount']);
  });

  it('sends the transfer and replaces the row with what the server says', () => {
    renderWithCommission({ account: null, invoices: [invoice()] });

    const input = elementOf<HTMLElement>(fixture, 'send-i1')!.querySelector('input')!;
    Object.defineProperty(input, 'files', {
      value: [new File([new Uint8Array([1, 2])], 'transfer.jpg', { type: 'image/jpeg' })],
    });
    input.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    const sent = httpMock.expectOne('/api/venues/v1/commission/i1/payment');
    expect(sent.request.body instanceof FormData).toBe(true);

    sent.flush(invoice({ status: 'PaymentSubmitted', hasEvidence: true }));
    fixture.detectChanges();

    expect(textOf(fixture, 'invoice-i1')).toContain(
      TRANSLATIONS.th['commission.status.PaymentSubmitted'],
    );
  });

  /** Showing the platform the transfer is the owner's alone (PRD US-14). */
  it('offers staff no way to say the platform has been paid', () => {
    renderWithCommission({ account: null, invoices: [invoice()] }, 'Staff');

    expect(elementOf(fixture, 'invoice-i1')).not.toBeNull();
    expect(elementOf(fixture, 'send-i1')).toBeNull();
  });

  function invoice(overrides: Record<string, unknown> = {}) {
    return {
      id: 'i1',
      venueId: 'v1',
      venueName: null,
      number: 'PLT-INV-2027-000001',
      month: '2027-01-01',
      amountBaht: 4000,
      status: 'Issued',
      overdue: false,
      issuedAt: '2027-02-02T02:00:00Z',
      dueOn: '2027-02-16',
      submittedAt: null,
      hasEvidence: false,
      paidAt: null,
      refusedReason: null,
      lines: [{ servedOn: '2027-01-10', keptBaht: 40000, percent: 10, amountBaht: 4000 }],
      ...overrides,
    };
  }

  /** Renders with a commission answer instead of the empty one render() flushes. */
  function renderWithCommission(owed: object, role: 'Owner' | 'Staff' = 'Owner'): void {
    signInAs(role === 'Owner' ? OWNER.email : STAFF.email);
    fixture = TestBed.createComponent(VenueDetailPage);
    fixture.componentRef.setInput('venueId', 'v1');
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1').flush(venueAs(role));
    httpMock.expectOne('/api/venues/v1/members').flush([OWNER, STAFF]);
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/v1/commission').flush(owed);
    fixture.detectChanges();
    if (role === 'Owner') {
      httpMock.expectOne('/api/venues/v1/invitations').flush([]);
      fixture.detectChanges();
    }
  }
});
