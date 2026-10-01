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
        // Every venue at once stands in the same frame (docs/plan/owner-app.md).
        { path: 'venues', children: [], data: { venueShell: 'all' } },
        // A venue's page is a venue's page because the route says so, not because its URL has an
        // id in it.
        { path: 'venues/:venueId/timeline', children: [], data: { venueShell: true } },
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

  const MINE = [
    { id: 'v1', name: 'Smash Court', role: 'Owner', permissions: [] },
    { id: 'v2', name: 'Second Court', role: 'Staff', permissions: [] },
  ];

  /**
   * The shell is deferred, so nothing of it exists until a venue's page asks for it. It asks who
   * this person is at each venue as it arrives, and the answer decides which sections it draws.
   */
  async function showShell(mine: object[] = MINE): Promise<void> {
    const blocks = await fixture.getDeferBlocks();
    await blocks[0].render(DeferBlockState.Complete);
    fixture.detectChanges();
    httpMock.expectOne('/api/venues/mine').flush(mine);
    fixture.detectChanges();
  }

  /** The four sections and nothing else: the pages that were under "อื่น ๆ" are gone. */
  it("draws the venue's four sections and nothing more", async () => {
    await router.navigate(['/venues', 'v1', 'timeline']);
    fixture.detectChanges();
    await showShell();

    for (const testId of ['nav-schedule', 'nav-pricing', 'nav-dashboard', 'nav-packages']) {
      expect(elementOf(fixture, testId)).not.toBeNull();
    }
    expect(elementOf(fixture, 'nav-more')).toBeNull();
    expect(elementOf(fixture, 'nav-slip-queue')).toBeNull();
    expect(elementOf(fixture, 'nav-money')).toBeNull();
    // The app's own doors travel with it, because the bar above is not drawn beside a sidebar.
    expect(elementOf(fixture, 'side-nav-account')).not.toBeNull();
  });

  it('leaves the pages outside a venue alone', async () => {
    // The class the shell hangs on is the whole answer: no venue, no second layout, and the
    // deferred rail is never asked for.
    const shellIsUp = () => (fixture.nativeElement as HTMLElement).classList.contains('at-a-venue');

    // Applying to join is not a venue's page, however much the URL looks like one.
    await router.navigate(['/venues/apply']);
    fixture.detectChanges();
    expect(shellIsUp()).toBe(false);

    await router.navigate(['/venues', 'v1', 'timeline']);
    fixture.detectChanges();
    expect(shellIsUp()).toBe(true);
    await showShell();
  });

  /*
   * The owner app (docs/plan/owner-app.md): the four sections, a switch between venues along the
   * top — with "every venue" only on the schedule — and the owner's two sections only for an
   * owner.
   */
  it('stands on every venue at once, with the schedule as its page', async () => {
    await router.navigate(['/venues']);
    fixture.detectChanges();
    await showShell();

    expect(textOf(fixture, 'owner-title')).toBe(TRANSLATIONS.th['nav.section.schedule']);
    expect(elementOf(fixture, 'nav-schedule')?.getAttribute('href')).toBe('/venues');
    expect(elementOf(fixture, 'pill-all')?.classList).toContain('on');
    // Revenue opens at a venue this person owns, never at one where they only work.
    expect(elementOf(fixture, 'nav-dashboard')?.getAttribute('href')).toBe('/venues/v1/dashboard');
  });

  it("keeps the owner's sections from somebody who only works at the venue", async () => {
    await router.navigate(['/venues', 'v2', 'timeline']);
    fixture.detectChanges();
    await showShell();

    expect(elementOf(fixture, 'nav-schedule')).not.toBeNull();
    expect(elementOf(fixture, 'nav-packages')).not.toBeNull();
    expect(elementOf(fixture, 'nav-dashboard')).toBeNull();
    expect(elementOf(fixture, 'nav-pricing')).toBeNull();
    // At a venue the schedule reads as a timeline or as right now.
    expect(elementOf(fixture, 'view-timeline')?.classList).toContain('on');
    expect(elementOf(fixture, 'view-now')?.getAttribute('href')).toBe('/venues/v2/now');
    // Switching venue keeps the page.
    expect(elementOf(fixture, 'pill-v1')?.getAttribute('href')).toBe('/venues/v1/timeline');
    // The day's list is gone: the schedule is where the day is run.
    expect(elementOf(fixture, 'nav-bookings')).toBeNull();
  });
});
