import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ApiError, AuthService, CurrentUser } from './auth.service';

const account: CurrentUser = {
  id: '11111111-1111-1111-1111-111111111111',
  email: 'player@example.com',
  emailConfirmed: false,
  language: 'th',
};

describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('signs in and then loads the account behind the session cookie', () => {
    let result: CurrentUser | null = null;
    service.login(account.email, 'CorrectHorse1').subscribe((user) => (result = user));

    httpMock.expectOne('/api/auth/login').flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne('/api/auth/me').flush(account);

    expect(result).toEqual(account);
    expect(service.currentUser()).toEqual(account);
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
  });
});
