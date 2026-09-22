import { Component, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../core/auth/auth.service';
import { TranslationService } from '../core/i18n/translation.service';

/**
 * The way in through LINE (PRD US-01, D6), offered only where this deployment has a channel for
 * it — so a stack without one does not show a button that goes nowhere.
 *
 * A link, not a button with a click handler: leaving the app for LINE is a navigation, and the
 * server has to set the cookie that ties the attempt to this browser before it goes anywhere.
 */
@Component({
  selector: 'app-line-sign-in',
  imports: [MatButtonModule],
  template: `
    @if (enabled()) {
      <div class="line-sign-in">
        <p class="line-or muted">{{ i18n.t('auth.line.or') }}</p>
        <a
          matButton="outlined"
          class="line-button"
          data-testid="line-sign-in"
          [href]="'/api/auth/line/start?returnUrl=' + encoded()"
        >
          {{ i18n.t('auth.line.continue') }}
        </a>
      </div>
    }
  `,
  styles: `
    .line-sign-in {
      display: grid;
      gap: 0.5rem;
      margin-block-start: 0.75rem;
    }

    /* A rule with the word on it, rather than a bare line: it says these are two ways in. */
    .line-or {
      display: grid;
      grid-template-columns: 1fr auto 1fr;
      align-items: center;
      gap: 0.5rem;
      margin: 0;
      font: var(--mat-sys-body-medium);

      &::before,
      &::after {
        content: '';
        border-block-start: 1px solid var(--mat-sys-outline-variant);
      }
    }

    .line-button {
      width: 100%;
    }
  `,
})
export class LineSignIn {
  protected readonly i18n = inject(TranslationService);

  private readonly auth = inject(AuthService);

  /** Where to come back to once LINE has answered. */
  readonly returnUrl = input('/');

  protected readonly enabled = toSignal(this.auth.lineEnabled(), { initialValue: false });

  protected encoded(): string {
    return encodeURIComponent(this.returnUrl());
  }
}
