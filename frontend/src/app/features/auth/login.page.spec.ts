import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TRANSLATIONS } from '../../core/i18n/locales';
import { pageProviders, setInput, submitForm, textOf } from '../../testing/dom';
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
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('h1')?.textContent?.trim()).toBe(TRANSLATIONS.en['login.title']);
  });
});
