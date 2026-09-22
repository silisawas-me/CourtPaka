import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslationService } from '../../core/i18n/translation.service';
import { TRANSLATIONS } from '../../testing/translations';
import {
  clickOn,
  elementOf,
  lineSignInAvailable,
  pageProviders,
  setInput,
  submitForm,
  textOf,
} from '../../testing/dom';
import { LoginPage } from './login.page';

describe('LoginPage', () => {
  let fixture: ComponentFixture<LoginPage>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [LoginPage],
      providers: pageProviders(),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(LoginPage);
    fixture.detectChanges();
    lineSignInAvailable(true);
  });

  afterEach(() => httpMock.verify());

  function fill(email: string, password: string): void {
    setInput(fixture, '#email', email);
    setInput(fixture, '#password', password);
  }

  it('does not call the API until the form is valid', () => {
    submitForm(fixture);

    expect(textOf(fixture, 'email-error')).toBe(TRANSLATIONS.th['common.required']);
    httpMock.expectNone('/api/auth/login');
  });

  it('shows the Thai message for the API error code', () => {
    fill('player@example.com', 'wrong-password');
    submitForm(fixture);

    httpMock
      .expectOne('/api/auth/login')
      .flush({ code: 'auth.invalid_credentials' }, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(textOf(fixture, 'form-error')).toBe(TRANSLATIONS.th['error.auth.invalid_credentials']);
  });

  it('adopts the language stored on the account after signing in', async () => {
    fill('player@example.com', 'CorrectHorse1');
    submitForm(fixture);

    httpMock.expectOne('/api/auth/login').flush(null, { status: 204, statusText: 'No Content' });
    httpMock.expectOne('/api/auth/me').flush({
      id: '11111111-1111-1111-1111-111111111111',
      email: 'player@example.com',
      emailConfirmed: true,
      language: 'en',
    });
    await fixture.whenStable();
    // The account's language is fetched when it is adopted; the page follows when it is here.
    await TestBed.inject(TranslationService).load('en');
    fixture.detectChanges();

    expect(textOf(fixture, 'page-title')).toBe(TRANSLATIONS.en['login.title']);
  });

  it('shows the password only while it is asked for', () => {
    const password = () =>
      (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#password')!;

    expect(password().type).toBe('password');

    clickOn(fixture, 'toggle-password');
    expect(password().type).toBe('text');

    clickOn(fixture, 'toggle-password');
    expect(password().type).toBe('password');
  });
  it('offers LINE, carrying the page the guard interrupted', () => {
    fixture.componentRef.setInput('returnUrl', '/bookings');
    fixture.detectChanges();

    const button = elementOf(fixture, 'line-sign-in') as HTMLAnchorElement;
    expect(button.getAttribute('href')).toBe(
      '/api/auth/line/start?returnUrl=' + encodeURIComponent('/bookings'),
    );
  });

  it('says why LINE sent the booker back here', () => {
    fixture.componentRef.setInput('line', 'auth.line_denied');
    fixture.detectChanges();

    expect(textOf(fixture, 'form-error')).toBe(TRANSLATIONS.th['error.auth.line_denied']);
  });
});
