import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { pageProviders, textOf } from '../../testing/dom';
import { VerifyEmailPage } from './verify-email.page';

function configure(queryParams: Record<string, string>) {
  TestBed.configureTestingModule({
    imports: [VerifyEmailPage],
    providers: [
      ...pageProviders(),
      {
        provide: ActivatedRoute,
        useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } },
      },
    ],
  });
}

describe('VerifyEmailPage', () => {
  afterEach(() => TestBed.resetTestingModule());

  it('confirms the account with the values from the link', () => {
    configure({ userId: '11111111-1111-1111-1111-111111111111', token: 'token-from-email' });
    const httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(VerifyEmailPage);
    fixture.detectChanges();

    const request = httpMock.expectOne('/api/auth/verify-email');
    expect(request.request.body).toEqual({
      userId: '11111111-1111-1111-1111-111111111111',
      token: 'token-from-email',
    });
    request.flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    // A signed-in visitor should see the new state at once, so the account is re-read.
    httpMock.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(textOf(fixture, 'verify-success')).toBe(TRANSLATIONS.th['verify.success']);
    httpMock.verify();
  });

  it('explains an expired or tampered link', () => {
    configure({ userId: '11111111-1111-1111-1111-111111111111', token: 'broken' });
    const httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(VerifyEmailPage);
    fixture.detectChanges();

    httpMock
      .expectOne('/api/auth/verify-email')
      .flush(
        { code: 'auth.invalid_verification_token' },
        { status: 400, statusText: 'Bad Request' },
      );
    fixture.detectChanges();

    expect(textOf(fixture, 'verify-error')).toBe(
      TRANSLATIONS.th['error.auth.invalid_verification_token'],
    );
    httpMock.verify();
  });

  it('does not call the API when the link has no token', () => {
    configure({});
    const httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(VerifyEmailPage);
    fixture.detectChanges();

    httpMock.expectNone('/api/auth/verify-email');
    expect(textOf(fixture, 'verify-error')).toBe(TRANSLATIONS.th['verify.linkInvalid']);
    httpMock.verify();
  });
});
