import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ApiError } from '../http/api-error';
import { TranslationService } from '../i18n/translation.service';
import { pageProviders } from '../../testing/dom';
import { AuthService, CurrentUser } from './auth.service';

const account: CurrentUser = {
  id: '11111111-1111-1111-1111-111111111111',
  email: 'player@example.com',
  emailConfirmed: false,
  language: 'en',
};

describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: pageProviders() });
    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('signs in, loads the account and applies its language', () => {
    let result: CurrentUser | null = null;
    service.login(account.email, 'CorrectHorse1').subscribe((user) => (result = user));

    httpMock.expectOne('/api/auth/login').flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne('/api/auth/me').flush(account);

    expect(result).toEqual(account);
    expect(service.currentUser()).toEqual(account);
    expect(TestBed.inject(TranslationService).language()).toBe('en');
  });

  it('reports a sign-in that left the browser without a session as a failure', () => {
    let error: unknown;
    let emitted = false;
    service.login(account.email, 'CorrectHorse1').subscribe({
      next: () => (emitted = true),
      error: (caught: unknown) => (error = caught),
    });

    httpMock.expectOne('/api/auth/login').flush(null, { status: 204, statusText: 'No Content' });
    // The password was accepted but the session cookie did not survive the round trip.
    httpMock.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(emitted).toBe(false);
    expect((error as ApiError).code).toBe('sessionNotEstablished');
  });

  it('signs the browser out even when the server rejects the request', () => {
    service.loadCurrentUser().subscribe();
    httpMock.expectOne('/api/auth/me').flush(account);

    let completed = false;
    service.logout().subscribe(() => (completed = true));
    httpMock.expectOne('/api/auth/logout').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(completed).toBe(true);
    expect(service.currentUser()).toBeNull();
  });

  it('turns an API error code into an ApiError', () => {
    let error: unknown;
    service
      .login(account.email, 'wrong')
      .subscribe({ error: (caught: unknown) => (error = caught) });

    httpMock
      .expectOne('/api/auth/login')
      .flush({ code: 'auth.invalid_credentials' }, { status: 401, statusText: 'Unauthorized' });

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).code).toBe('auth.invalid_credentials');
  });

  it('reports rate limiting with its own code', () => {
    let error: unknown;
    service
      .resendVerification(account.email)
      .subscribe({ error: (caught: unknown) => (error = caught) });

    httpMock
      .expectOne('/api/auth/resend-verification')
      .flush(null, { status: 429, statusText: 'Too Many Requests' });

    expect((error as ApiError).code).toBe('tooManyRequests');
  });

  it('treats 401 from /me as signed out rather than an error', () => {
    let result: CurrentUser | null | undefined;
    service.loadCurrentUser().subscribe((user) => (result = user));

    httpMock.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(result).toBeNull();
    expect(service.currentUser()).toBeNull();
    expect(service.ready()).toBe(true);
  });

  it('fetches the privacy policy version once and shares it', () => {
    const versions: string[] = [];
    service.privacyPolicyVersion().subscribe((version) => versions.push(version));
    httpMock.expectOne('/api/auth/privacy-policy').flush({ version: '2026-09-01' });

    service.privacyPolicyVersion().subscribe((version) => versions.push(version));

    expect(versions).toEqual(['2026-09-01', '2026-09-01']);
    httpMock.expectNone('/api/auth/privacy-policy');
  });

  it('keeps the account language in step when it is changed', () => {
    service.loadCurrentUser().subscribe();
    httpMock.expectOne('/api/auth/me').flush(account);

    service.changeLanguage('th').subscribe();
    httpMock
      .expectOne('/api/auth/me/language')
      .flush(null, { status: 204, statusText: 'No Content' });

    expect(service.currentUser()?.language).toBe('th');
  });
});
