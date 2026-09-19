import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { pageProviders, textOf } from '../../testing/dom';
import { AcceptInvitationPage } from './accept-invitation.page';

function configure(queryParams: Record<string, string>) {
  TestBed.configureTestingModule({
    imports: [AcceptInvitationPage],
    providers: [
      ...pageProviders(),
      {
        provide: ActivatedRoute,
        useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } },
      },
    ],
  });
}

function signIn(): void {
  const httpMock = TestBed.inject(HttpTestingController);
  TestBed.inject(AuthService).loadCurrentUser().subscribe();
  httpMock
    .expectOne('/api/auth/me')
    .flush({ id: 'u1', email: 'staff@example.com', emailConfirmed: true, language: 'th' });
}

describe('AcceptInvitationPage', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('asks anonymous visitors to sign in first', () => {
    configure({ invitationId: 'i1', token: 't' });
    const httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AcceptInvitationPage);
    fixture.detectChanges();

    expect(textOf(fixture, 'accept-sign-in')).toBe(TRANSLATIONS.th['venues.accept.signInFirst']);
    httpMock.expectNone('/api/venues/invitations/accept');
    httpMock.verify();
  });

  it('joins the venue with the values from the link', () => {
    configure({ invitationId: 'i1', token: 'token-from-email' });
    const httpMock = TestBed.inject(HttpTestingController);
    signIn();
    const fixture = TestBed.createComponent(AcceptInvitationPage);
    fixture.detectChanges();

    const request = httpMock.expectOne('/api/venues/invitations/accept');
    expect(request.request.body).toEqual({ invitationId: 'i1', token: 'token-from-email' });
    request.flush({ id: 'v1', code: 'SBC', name: 'Smash Court', status: 'Approved' });
    fixture.detectChanges();

    expect(textOf(fixture, 'accept-success')).toContain('Smash Court');
    httpMock.verify();
  });

  it('explains an invitation that belongs to another address', () => {
    configure({ invitationId: 'i1', token: 'token-from-email' });
    const httpMock = TestBed.inject(HttpTestingController);
    signIn();
    const fixture = TestBed.createComponent(AcceptInvitationPage);
    fixture.detectChanges();

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
    httpMock.verify();
  });

  it('does not call the API when the link is incomplete', () => {
    configure({});
    const httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AcceptInvitationPage);
    fixture.detectChanges();

    httpMock.expectNone('/api/venues/invitations/accept');
    expect(textOf(fixture, 'accept-error')).toBe(TRANSLATIONS.th['venues.accept.invalid']);
    httpMock.verify();
  });
});
