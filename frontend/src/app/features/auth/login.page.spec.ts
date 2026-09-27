import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { TranslationService } from '../../core/i18n/translation.service';
import { TRANSLATIONS } from '../../testing/translations';
import { clickOn, elementOf, pageProviders, setInput, submitForm, textOf } from '../../testing/dom';
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

  /* The wordmark is the page's one h1; the thing you came to do is the heading under it. */
  it('puts the form heading under the wordmark', () => {
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelectorAll('h1').length).toBe(1);
    expect(elementOf(fixture, 'page-title')?.tagName).toBe('H2');
  });

  /*
   * The two doors into the venue side (docs/plan/cut-booker.md, D12–D15). Which door is theirs is
   * read from the roles the server gave per venue; the wrong one signs them out again and says
   * which door to use.
   */
  describe('the two doors', () => {
    const me = {
      id: '11111111-1111-1111-1111-111111111111',
      email: 'someone@example.com',
      emailConfirmed: true,
      language: 'th',
      isPlatformAdmin: false,
    };

    function signIn(door: string, venues: object[]): void {
      fixture.componentRef.setInput('as', door);
      fixture.detectChanges();
      fill('someone@example.com', 'CorrectHorse1');
      submitForm(fixture);
      httpMock.expectOne('/api/auth/login').flush(null, { status: 204, statusText: 'No Content' });
      httpMock.expectOne('/api/auth/me').flush(me);
      httpMock.expectOne('/api/venues/mine').flush(venues);
    }

    it('names the door on the card', () => {
      fixture.componentRef.setInput('as', 'staff');
      fixture.detectChanges();

      expect(textOf(fixture, 'page-title')).toBe(TRANSLATIONS.th['login.door.staff.title']);
      // The way to the other door says which door it is, not which one this is.
      const other = elementOf(fixture, 'other-door');
      expect(other?.getAttribute('href')).toBe('/login?as=admin');
      expect(other?.textContent?.trim()).toBe(TRANSLATIONS.th['login.door.admin.switch']);
    });

    it('turns staff away from the admin door, signed out, with the door that is theirs', () => {
      signIn('admin', [{ id: 'v1', role: 'Staff' }]);
      httpMock.expectOne('/api/auth/logout').flush(null, { status: 204, statusText: 'No Content' });
      fixture.detectChanges();

      expect(textOf(fixture, 'form-error')).toBe(TRANSLATIONS.th['login.door.notAnOwner']);
    });

    it('takes staff with one venue to its timeline', async () => {
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

      signIn('staff', [{ id: 'v1', role: 'Staff' }]);

      expect(navigate).toHaveBeenCalledWith('/venues/v1/bookings');
    });

    it('offers a new account only to somebody sent here by an invitation', () => {
      expect(elementOf(fixture, 'register-link')).toBeNull();

      fixture.componentRef.setInput('returnUrl', '/venue-invitation?token=abc');
      fixture.detectChanges();

      expect(elementOf(fixture, 'register-link')).not.toBeNull();
    });
  });
});
