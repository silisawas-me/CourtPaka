import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { LoginPage } from './login.page';

describe('LoginPage', () => {
  let fixture: ComponentFixture<LoginPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [LoginPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LoginPage);
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  function fill(email: string, password: string): void {
    const element = fixture.nativeElement as HTMLElement;
    const emailInput = element.querySelector<HTMLInputElement>('#email')!;
    const passwordInput = element.querySelector<HTMLInputElement>('#password')!;
    emailInput.value = email;
    emailInput.dispatchEvent(new Event('input'));
    passwordInput.value = password;
    passwordInput.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function submit(): void {
    const element = fixture.nativeElement as HTMLElement;
    element.querySelector('form')!.dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  }

  function textOf(testId: string): string | undefined {
    const element = fixture.nativeElement as HTMLElement;
    return element.querySelector(`[data-testid="${testId}"]`)?.textContent?.trim();
  }

  it('does not call the API until the form is valid', () => {
    submit();

    expect(textOf('email-error')).toBe(TRANSLATIONS.th['common.required']);
    httpMock.expectNone('/api/auth/login');
  });

  it('shows the Thai message for the API error code', () => {
    fill('player@example.com', 'wrong-password');
    submit();

    httpMock
      .expectOne('/api/auth/login')
      .flush({ code: 'auth.account_locked' }, { status: 423, statusText: 'Locked' });
    fixture.detectChanges();

    expect(textOf('form-error')).toBe(TRANSLATIONS.th['error.auth.account_locked']);
  });

  it('adopts the language stored on the account after signing in', async () => {
    fill('player@example.com', 'CorrectHorse1');
    submit();

    httpMock.expectOne('/api/auth/login').flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne('/api/auth/me').flush({
      id: '11111111-1111-1111-1111-111111111111',
      email: 'player@example.com',
      emailConfirmed: true,
      language: 'en',
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('h1')?.textContent?.trim()).toBe(TRANSLATIONS.en['login.title']);
  });
});
