import { HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, DeferBlockState, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { App } from './app';
import { AuthService } from './core/auth/auth.service';
import { TranslationService } from './core/i18n/translation.service';
import { TRANSLATIONS } from './testing/translations';
import { check, clickOn, elementOf, pageProviders, signInAs, textOf } from './testing/dom';

describe('App shell', () => {
  let fixture: ComponentFixture<App>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [App],
      // The panel test follows a link, and a navigation with nowhere to go rejects in the background.
      providers: pageProviders([{ path: 'venues', children: [] }]),
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
    // signed in, so that is Venues, Account and Sign out.
    const inBar = (fixture.nativeElement as HTMLElement).querySelectorAll('.bar-links > *');
    const inPanel = (fixture.nativeElement as HTMLElement).querySelectorAll('.bar-menu > *');
    expect(inPanel).toHaveLength(inBar.length);
    expect(elementOf(fixture, 'nav-venues-menu')).not.toBeNull();
    // The booker's doors are gone (docs/plan/cut-booker.md).
    expect(elementOf(fixture, 'nav-book-menu')).toBeNull();
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

    clickOn(fixture, 'nav-venues-menu');

    expect(elementOf(fixture, 'bar-menu')).toBeNull();
  });
});

describe("A venue's own shell", () => {
  let fixture: ComponentFixture<App>;
  let httpMock: HttpTestingController;
  let router: Router;

  beforeEach(async () => {
    localStorage.clear();
    await TestBed.configureTestingModule({
      imports: [App],
      providers: pageProviders([
        { path: 'venues', children: [] },
        // A venue's page is a venue's page because the route says so, not because its URL has an
        // id in it — `book/:venueId` is the booker's grid and carries the same parameter.
        {
          path: 'venues/:venueId/bookings',
          children: [],
          data: { venueShell: true },
        },
        { path: 'venues/:venueId/money', children: [], data: { venueShell: true } },
        { path: 'venues/apply', children: [] },
      ]),
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    signInAs('staff@example.com');
    router = TestBed.inject(Router);

    fixture = TestBed.createComponent(App);
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  /** The shell is deferred, so nothing of it exists until a venue's page asks for it. */
  async function showShell(): Promise<void> {
    const blocks = await fixture.getDeferBlocks();
    await blocks[0].render(DeferBlockState.Complete);
    fixture.detectChanges();
  }

  /** The counts are what decide where a shift looks next (PRD US-17), so they come with the shell. */
  it("draws the venue's doors with what is waiting behind them", async () => {
    await router.navigate(['/venues', 'v1', 'bookings']);
    fixture.detectChanges();
    await showShell();

    httpMock.expectOne('/api/venues/v1/attention').flush({
      slipsToCheck: 3,
      bookingsWithMoneyWaiting: 0,
    });
    fixture.detectChanges();

    expect(textOf(fixture, 'nav-slip-queue-waiting')).toBe('3');
    // Nothing waiting is no number at all, not a zero to read past.
    expect(elementOf(fixture, 'nav-money-waiting')).toBeNull();
    expect(elementOf(fixture, 'nav-slip-queue')?.getAttribute('href')).toBe(
      '/venues/v1/slip-queue',
    );
    // The app's own doors travel with it, because the bar above is not drawn beside a sidebar.
    expect(elementOf(fixture, 'side-nav-account')).not.toBeNull();
  });

  it('leaves the pages outside a venue alone', async () => {
    // The class the shell hangs on is the whole answer: no venue, no second layout, and the
    // deferred sidebar is never asked for.
    const shellIsUp = () => (fixture.nativeElement as HTMLElement).classList.contains('at-a-venue');

    await router.navigate(['/venues']);
    fixture.detectChanges();
    expect(shellIsUp()).toBe(false);

    // Neither is applying to join, however much the URL looks like a venue's.
    await router.navigate(['/venues/apply']);
    fixture.detectChanges();
    expect(shellIsUp()).toBe(false);

    await router.navigate(['/venues', 'v1', 'bookings']);
    fixture.detectChanges();
    expect(shellIsUp()).toBe(true);
    await showShell();
    httpMock.expectOne('/api/venues/v1/attention').flush({
      slipsToCheck: 0,
      bookingsWithMoneyWaiting: 0,
    });
  });

  /**
   * The number says which door to open next, so it has to be read again once somebody has been
   * through one — a count that answers for the state an hour ago is worse than no count.
   */
  it('reads the counts again as the shift moves between the doors', async () => {
    await router.navigate(['/venues', 'v1', 'bookings']);
    fixture.detectChanges();
    await showShell();

    httpMock.expectOne('/api/venues/v1/attention').flush({
      slipsToCheck: 3,
      bookingsWithMoneyWaiting: 0,
    });
    fixture.detectChanges();
    expect(textOf(fixture, 'nav-slip-queue-waiting')).toBe('3');

    await router.navigate(['/venues', 'v1', 'money']);
    fixture.detectChanges();

    httpMock.expectOne('/api/venues/v1/attention').flush({
      slipsToCheck: 0,
      bookingsWithMoneyWaiting: 0,
    });
    fixture.detectChanges();

    expect(elementOf(fixture, 'nav-slip-queue-waiting')).toBeNull();
  });

  /** A venue whose counts cannot be read still has doors; only the numbers go missing. */
  it('draws the doors even when the counts cannot be read', async () => {
    await router.navigate(['/venues', 'v2', 'money']);
    fixture.detectChanges();
    await showShell();

    httpMock
      .expectOne('/api/venues/v2/attention')
      .flush(null, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();

    expect(elementOf(fixture, 'nav-money')).not.toBeNull();
    expect(elementOf(fixture, 'nav-money-waiting')).toBeNull();
  });
});
