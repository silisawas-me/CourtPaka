import { Component, computed, inject, signal } from '@angular/core';
import { MatAnchor, MatButton } from '@angular/material/button';
import { MatToolbar } from '@angular/material/toolbar';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { AuthService } from './core/auth/auth.service';
import { Language, LANGUAGES } from './core/i18n/locales';
import { TranslationService } from './core/i18n/translation.service';

@Component({
  // The directives, not the modules: MatButtonModule also declares icon and fab buttons,
  // which the shell does not use but would carry into the first chunk.
  imports: [RouterOutlet, RouterLink, MatToolbar, MatButton, MatAnchor],
  host: {
    // Escape closes the panel wherever the focus happens to be inside it.
    '(document:keydown.escape)': 'closeMenu()',
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
