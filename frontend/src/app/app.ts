import { Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatAnchor, MatButton } from '@angular/material/button';
import { MatToolbar } from '@angular/material/toolbar';
import {
  ActivatedRouteSnapshot,
  NavigationEnd,
  Router,
  RouterLink,
  RouterOutlet,
} from '@angular/router';
import { filter, map } from 'rxjs';
import { AuthService } from './core/auth/auth.service';
import { Language, LANGUAGES } from './core/i18n/locales';
import { TranslationService } from './core/i18n/translation.service';
import { VenueShell } from './shared/venue-shell';

import { Wordmark } from './shared/wordmark';
@Component({
  // The directives, not the modules: MatButtonModule also declares icon and fab buttons,
  // which the shell does not use but would carry into the first chunk.
  imports: [RouterOutlet, RouterLink, MatToolbar, MatButton, MatAnchor, VenueShell, Wordmark],
  host: {
    // Escape closes the panel wherever the focus happens to be inside it.
    '(document:keydown.escape)': 'closeMenu()',
    // The venue's own pages get their own shell: a sidebar on a desk, a bar along the bottom of
    // a phone. The booker's pages keep the top bar, because they are a different kind of visit.
    '[class.at-a-venue]': 'venueId() !== null',
  },
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly i18n = inject(TranslationService);
  protected readonly languages = LANGUAGES;
  protected readonly user = this.auth.currentUser;
  /**
   * What the bar links to, in one list: the bar lays it out and the menu folds it up, so a link
   * added here appears in both without either copy being the one someone forgot.
   */
  protected readonly links = computed(() => [
    { path: '/book', label: 'nav.book', testId: 'nav-book', accent: false },
    ...(this.user()
      ? [
          {
            path: '/bookings',
            label: 'nav.myBookings',
            testId: 'nav-my-bookings',
            accent: false,
          },
          { path: '/venues', label: 'nav.venues', testId: 'nav-venues', accent: false },
          { path: '/account', label: 'nav.account', testId: 'nav-account', accent: false },
          // Drawn only for the handful of people it is for. The endpoints behind it check
          // again, so this is about not showing a door that would not open (PRD US-20).
          ...(this.user()?.isPlatformAdmin
            ? [
                {
                  path: '/admin/venues',
                  label: 'nav.admin',
                  testId: 'nav-admin',
                  accent: false,
                },
              ]
            : []),
        ]
      : [
          { path: '/login', label: 'nav.signIn', testId: 'nav-sign-in', accent: false },
          { path: '/register', label: 'nav.signUp', testId: 'nav-sign-up', accent: true },
        ]),
  ]);

  /**
   * The venue whose pages are on screen, from what the route says it is rather than from the
   * shape of the URL: `book/:venueId` is a booker's grid and carries the same parameter, so a
   * search for the parameter alone would put a counter's sidebar on the public page.
   */
  protected readonly venueId = toSignal(
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      map(() => venueOf(this.router.routerState.snapshot.root)),
    ),
    { initialValue: venueOf(this.router.routerState.snapshot.root) },
  );

  /** Whether the phone's nav panel is open. There is no panel at all on a wider screen. */
  protected readonly menuOpen = signal(false);

  protected readonly languageNotSaved = signal(false);
  protected readonly signOutIncomplete = signal(false);

  protected toggleMenu(): void {
    this.menuOpen.update((open) => !open);
  }

  protected closeMenu(): void {
    this.menuOpen.set(false);
  }

  protected switchLanguage(language: Language): void {
    this.i18n.use(language);
    this.languageNotSaved.set(false);

    // Signed-in users keep the choice on their account so emails use it too (PRD US-06).
    if (this.user()) {
      this.auth.changeLanguage(language).subscribe({
        // Saying nothing would let the browser and the account disagree without the user knowing.
        error: () => this.languageNotSaved.set(true),
      });
    }
  }

  protected signOut(): void {
    this.closeMenu();
    this.auth.logout().subscribe(({ confirmed }) => {
      this.signOutIncomplete.set(!confirmed);
      void this.router.navigate(['/']);
    });
  }
}

/**
 * The venue whose shell the matched route asked for, or null. A route says so with
 * `data: { venueShell: true }`, which is the only thing that separates a venue's own page from
 * the booker's grid — both carry a `venueId`.
 */
function venueOf(route: ActivatedRouteSnapshot | null): string | null {
  while (route) {
    const venueId = route.params['venueId'];
    if (route.data['venueShell'] === true && typeof venueId === 'string' && venueId.length > 0) {
      return venueId;
    }
    route = route.firstChild;
  }

  return null;
}
