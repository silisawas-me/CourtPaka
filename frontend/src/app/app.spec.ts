import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { App } from './app';
import { AuthService } from './core/auth/auth.service';
import { TRANSLATIONS } from './core/i18n/locales';
import { pageProviders, textOf } from './testing/dom';

describe('App shell', () => {
  let fixture: ComponentFixture<App>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [App],
      providers: pageProviders(),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    // The real app resolves the session before bootstrap; do the same here.
    TestBed.inject(AuthService).loadCurrentUser().subscribe();
    httpMock.expectOne('/api/auth/me').flush({
      id: '11111111-1111-1111-1111-111111111111',
      email: 'player@example.com',
      emailConfirmed: true,
      language: 'th',
    });

    fixture = TestBed.createComponent(App);
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  it('saves the language on the account when a signed-in user switches', () => {
    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="language-en"]')!
      .click();
    fixture.detectChanges();

    const request = httpMock.expectOne('/api/auth/me/language');
    expect(request.request.body).toEqual({ language: 'en' });
    request.flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(textOf(fixture, 'language-not-saved')).toBeUndefined();
  });

  it('says so when the language could not be saved to the account', () => {
    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('[data-testid="language-en"]')!
      .click();
    fixture.detectChanges();

    httpMock
      .expectOne('/api/auth/me/language')
      .flush(null, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    expect(textOf(fixture, 'language-not-saved')).toBe(TRANSLATIONS.en['app.languageNotSaved']);
  });
});
