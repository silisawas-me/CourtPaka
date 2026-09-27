import { Component, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { TranslationService } from '../../core/i18n/translation.service';
import { Alpaca } from '../../shared/alpaca';
import { CourtArt } from '../../shared/court-art';

/**
 * The way in. Somebody arriving is one of two people — they have been here before, or they have
 * not — and the page asks that once rather than showing every door at once and leaving them to
 * work it out. Somebody already signed in is asked neither: the doors become where they were
 * going.
 */
import { Wordmark } from '../../shared/wordmark';
@Component({
  selector: 'app-home-page',
  imports: [RouterLink, MatButtonModule, Alpaca, CourtArt, Wordmark],
  templateUrl: './home.page.html',
  styleUrl: './home.page.scss',
})
export class HomePage {
  private readonly auth = inject(AuthService);

  protected readonly i18n = inject(TranslationService);
  protected readonly user = this.auth.currentUser;

  /** Set when the account page has just forgotten somebody, so they see that it happened. */
  readonly deleted = input<string>();
}
