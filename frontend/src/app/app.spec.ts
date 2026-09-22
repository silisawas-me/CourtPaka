import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { App } from './app';
import { AuthService } from './core/auth/auth.service';
import { TranslationService } from './core/i18n/translation.service';
import { TRANSLATIONS } from './testing/translations';
import { check, clickOn, elementOf, pageProviders, textOf } from './testing/dom';

describe('App shell', () => {
  let fixture: ComponentFixture<App>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [App],
      // The panel test follows a link, and a navigation with nowhere to go rejects in the background.
      providers: pageProviders([{ path: 'book', children: [] }]),
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
    check(fixture, '[data-testid="language-en"]');

    const request = httpMock.expectOne('/api/auth/me/language');
    expect(request.request.body).toEqual({ language: 'en' });
    request.flush(null, { status: 204, statusText: 'No Content' });
    fixture.detectChanges();

    expect(textOf(fixture, 'language-not-saved')).toBeUndefined();
  });

  it('says so when the language could not be saved to the account', async () => {
    check(fixture, '[data-testid="language-en"]');

    httpMock
      .expectOne('/api/auth/me/language')
      .flush(null, { status: 500, statusText: 'Server Error' });
    // English is fetched when it is chosen, so the message arrives in it once it is here.
    await TestBed.inject(TranslationService).load('en');
    fixture.detectChanges();

    expect(textOf(fixture, 'language-not-saved')).toBe(TRANSLATIONS.en['app.languageNotSaved']);
  });

  it('folds the links into a panel that opens and closes', () => {
    // The panel is the phone's copy of the nav; CSS decides which copy is on screen, so the test
    // checks the behaviour rather than the width.
    expect(elementOf(fixture, 'bar-menu')).toBeNull();

    clickOn(fixture, 'open-menu');
    expect(elementOf(fixture, 'bar-menu')).not.toBeNull();
    expect(elementOf(fixture, 'open-menu')?.getAttribute('aria-expanded')).toBe('true');
    // Whatever the bar carries, the panel carries: one list rendered twice. The session here is
    // signed in, so that is Book, Venues and Sign out.
    const inBar = (fixture.nativeElement as HTMLElement).querySelectorAll('.bar-links > *');
    const inPanel = (fixture.nativeElement as HTMLElement).querySelectorAll('.bar-menu > *');
    expect(inPanel).toHaveLength(inBar.length);
    expect(elementOf(fixture, 'nav-book-menu')).not.toBeNull();
    expect(elementOf(fixture, 'nav-venues-menu')).not.toBeNull();
    expect(elementOf(fixture, 'sign-out-menu')).not.toBeNull();

    clickOn(fixture, 'open-menu');
    expect(elementOf(fixture, 'bar-menu')).toBeNull();
  });

  it('closes the panel on Escape, wherever the focus is', () => {
    clickOn(fixture, 'open-menu');
    expect(elementOf(fixture, 'bar-menu')).not.toBeNull();

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();

    expect(elementOf(fixture, 'bar-menu')).toBeNull();
  });

  it('closes the panel when a link in it is followed', () => {
    clickOn(fixture, 'open-menu');

    clickOn(fixture, 'nav-book-menu');

    expect(elementOf(fixture, 'bar-menu')).toBeNull();
  });
});
