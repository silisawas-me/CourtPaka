import { Component, inject, OnInit } from '@angular/core';
import { Router, RouterLink, RouterOutlet } from '@angular/router';
import { AuthService } from './core/auth/auth.service';
import { isLanguage, Language, LANGUAGES } from './core/i18n/locales';
import { TranslationService } from './core/i18n/translation.service';
import { TranslatePipe } from './core/i18n/translate.pipe';

@Component({
  imports: [RouterOutlet, RouterLink, TranslatePipe],
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly translations = inject(TranslationService);
  private readonly router = inject(Router);

  protected readonly languages = LANGUAGES;
  protected readonly language = this.translations.language;
  protected readonly user = this.auth.currentUser;

  ngOnInit(): void {
    // A session cookie may already exist from a previous visit.
    this.auth.loadCurrentUser().subscribe({
      next: (user) => {
        if (user && isLanguage(user.language)) {
          this.translations.use(user.language);
        }
      },
      error: () => undefined,
    });
  }

  protected switchLanguage(language: Language): void {
    this.translations.use(language);
    // Signed-in users keep the choice on their account so emails use it too (PRD US-06).
    if (this.user()) {
      this.auth.changeLanguage(language).subscribe({ error: () => undefined });
    }
  }

  protected signOut(): void {
    this.auth.logout().subscribe({
      next: () => void this.router.navigate(['/']),
      error: () => undefined,
    });
  }
}
