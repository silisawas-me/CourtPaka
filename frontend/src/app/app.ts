import { Component, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { AuthService } from './core/auth/auth.service';
import { Language, LANGUAGES } from './core/i18n/locales';
import { TranslationService } from './core/i18n/translation.service';

@Component({
  imports: [RouterOutlet, RouterLink],
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
  protected readonly languageNotSaved = signal(false);

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
    this.auth.logout().subscribe({ next: () => void this.router.navigate(['/']) });
  }
}
