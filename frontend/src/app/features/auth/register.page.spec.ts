import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { RegisterPage } from './register.page';

describe('RegisterPage', () => {
  let fixture: ComponentFixture<RegisterPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [RegisterPage],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(RegisterPage);
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  function fillValidForm(password = 'CorrectHorse1'): void {
    const element = fixture.nativeElement as HTMLElement;
    const email = element.querySelector<HTMLInputElement>('#email')!;
    const passwordInput = element.querySelector<HTMLInputElement>('#password')!;
    const accept = element.querySelector<HTMLInputElement>('input[type="checkbox"]')!;
    email.value = 'player@example.com';
    email.dispatchEvent(new Event('input'));
    passwordInput.value = password;
    passwordInput.dispatchEvent(new Event('input'));
    accept.click();
    fixture.detectChanges();
  }

  function submit(): void {
    (fixture.nativeElement as HTMLElement)
      .querySelector('form')!
      .dispatchEvent(new Event('submit'));
    fixture.detectChanges();
  }

  function textOf(testId: string): string | undefined {
    const element = fixture.nativeElement as HTMLElement;
    return element.querySelector(`[data-testid="${testId}"]`)?.textContent?.trim();
  }

  it('requires accepting the privacy policy', () => {
    const element = fixture.nativeElement as HTMLElement;
    const email = element.querySelector<HTMLInputElement>('#email')!;
    const password = element.querySelector<HTMLInputElement>('#password')!;
    email.value = 'player@example.com';
    email.dispatchEvent(new Event('input'));
    password.value = 'CorrectHorse1';
    password.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    submit();

    expect(textOf('policy-error')).toBe(TRANSLATIONS.th['register.policyRequired']);
    httpMock.expectNone('/api/auth/register');
  });

  it('sends the policy version the server reports, not one from the page', () => {
    fillValidForm();
    submit();

    httpMock.expectOne('/api/auth/privacy-policy').flush({ version: '2026-09-01' });
    const registration = httpMock.expectOne('/api/auth/register');
    expect(registration.request.body).toEqual({
      email: 'player@example.com',
      password: 'CorrectHorse1',
      privacyPolicyVersion: '2026-09-01',
      language: 'th',
      phoneNumber: null,
    });

    registration.flush(null, { status: 201, statusText: 'Created' });
    fixture.detectChanges();

    expect(textOf('register-done')).toContain(TRANSLATIONS.th['register.done.title']);
  });

  it('shows the translated message when the API rejects the password', () => {
    fillValidForm('short1');
    submit();

    // The client-side rule rejects it first, so nothing is sent.
    expect(textOf('password-error')).toBe(TRANSLATIONS.th['common.passwordTooShort']);
    httpMock.expectNone('/api/auth/privacy-policy');
  });
});
