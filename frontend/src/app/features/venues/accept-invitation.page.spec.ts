import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { pageProviders, signInAs, textOf } from '../../testing/dom';
import { AcceptInvitationPage } from './accept-invitation.page';

describe('AcceptInvitationPage', () => {
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [AcceptInvitationPage],
      providers: pageProviders([{ path: 'venues/:venueId', children: [] }]),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  function render(inputs: {
    invitationId?: string;
    token?: string;
  }): ComponentFixture<AcceptInvitationPage> {
    const fixture = TestBed.createComponent(AcceptInvitationPage);
    fixture.componentRef.setInput('invitationId', inputs.invitationId);
    fixture.componentRef.setInput('token', inputs.token);
    fixture.detectChanges();
    return fixture;
  }

  it('joins the venue with the values from the link', () => {
    signInAs('staff@example.com');
    const fixture = render({ invitationId: 'i1', token: 'token-from-email' });

    const request = httpMock.expectOne('/api/venues/invitations/accept');
    expect(request.request.body).toEqual({ invitationId: 'i1', token: 'token-from-email' });
    request.flush({
      id: 'v1',
      code: 'SBC',
      name: 'Smash Court',
      status: 'Approved',
      role: 'Staff',
      permissions: ['VerifySlip'],
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'accept-success')).toContain('Smash Court');
    // The account gained a venue, so the session is re-read.
    httpMock.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });
  });

  it('explains an invitation that belongs to another address', () => {
    signInAs('someone-else@example.com');
    const fixture = render({ invitationId: 'i1', token: 'token-from-email' });

    httpMock
      .expectOne('/api/venues/invitations/accept')
      .flush(
        { code: 'venue.invitation_for_another_address' },
        { status: 403, statusText: 'Forbidden' },
      );
    fixture.detectChanges();

    expect(textOf(fixture, 'accept-error')).toBe(
      TRANSLATIONS.th['error.venue.invitation_for_another_address'],
    );
  });

  it('does not call the API when the link is incomplete', () => {
    signInAs('staff@example.com');
    const fixture = render({});

    httpMock.expectNone('/api/venues/invitations/accept');
    expect(textOf(fixture, 'accept-error')).toBe(TRANSLATIONS.th['venues.accept.invalid']);
  });
});
