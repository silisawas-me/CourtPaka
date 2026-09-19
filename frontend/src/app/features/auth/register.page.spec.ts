import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { check, pageProviders, setInput, submitForm, textOf } from '../../testing/dom';
import { RegisterPage } from './register.page';

describe('RegisterPage', () => {
  let fixture: ComponentFixture<RegisterPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [RegisterPage],
      providers: pageProviders(),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(RegisterPage);
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  /** The page prefetches the policy version on init, so every test answers that request first. */
  function answerPolicyVersion(version = '2026-09-01'): void {
    httpMock.expectOne('/api/auth/privacy-policy').flush({ version });
  }

  function fillValidForm(password = 'CorrectHorse1'): void {
    setInput(fixture, '#email', 'player@example.com');
    setInput(fixture, '#password', password);
    check(fixture, 'input[type="checkbox"]');
  }

  it('requires accepting the privacy policy', () => {
    answerPolicyVersion();
    setInput(fixture, '#email', 'player@example.com');
    setInput(fixture, '#password', 'CorrectHorse1');

    submitForm(fixture);

    expect(textOf(fixture, 'policy-error')).toBe(TRANSLATIONS.th['register.policyRequired']);
    httpMock.expectNone('/api/auth/register');
  });

  it('sends the policy version the server reports, not one from the page', () => {
    answerPolicyVersion();
    fillValidForm();

    submitForm(fixture);

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

    expect(textOf(fixture, 'register-done')).toContain(TRANSLATIONS.th['register.done.title']);
  });

  it('translates an API rejection', () => {
    answerPolicyVersion();
    fillValidForm();
    submitForm(fixture);

    httpMock
      .expectOne('/api/auth/register')
      .flush({ code: 'auth.weak_password' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(textOf(fixture, 'form-error')).toBe(TRANSLATIONS.th['error.auth.weak_password']);
  });

  it('rejects a short password before calling the API', () => {
    answerPolicyVersion();
    fillValidForm('short1');

    submitForm(fixture);

    expect(textOf(fixture, 'password-error')).toBe(TRANSLATIONS.th['common.passwordTooShort']);
    httpMock.expectNone('/api/auth/register');
  });
});
