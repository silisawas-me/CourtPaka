import { Component, computed, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconButton } from '@angular/material/button';
import { MatInputModule } from '@angular/material/input';
import { Router, RouterLink } from '@angular/router';
import { map, switchMap } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { VenueService } from '../../core/venues/venue.service';
import { Alpaca } from '../../shared/alpaca';
import { CourtArt } from '../../shared/court-art';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { Wordmark } from '../../shared/wordmark';
import { Door, isDoor, landing, refusal } from './doors';

@Component({
  selector: 'app-login-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    FieldError,
    Wordmark,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    Alpaca,
    CourtArt,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './login.page.html',
  styleUrl: './login.page.scss',
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly venues = inject(VenueService);

  protected readonly i18n = inject(TranslationService);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', Validators.required],
    password: ['', Validators.required],
  });

  /**
   * Whether the password is readable. A password nobody can check is how a sign-in fails twice,
   * and it starts hidden because someone may be standing behind them.
   */
  protected readonly passwordShown = signal(false);

  protected togglePassword(): void {
    this.passwordShown.update((shown) => !shown);
  }

  /** Set by the auth guard when it interrupts a page that needs an account. */
  readonly returnUrl = input<string>();

  /** Which of the two doors this is (`?as=admin` or `?as=staff`), or neither. */
  readonly as = input<string>();

  protected readonly door = computed<Door | null>(() => {
    const asked = this.as();
    return isDoor(asked) ? asked : null;
  });

  /** The other door, offered to somebody who came to the wrong one. */
  protected readonly otherDoor = computed<Door | null>(() => {
    const door = this.door();
    return door === null ? null : door === 'admin' ? 'staff' : 'admin';
  });

  /**
   * Somebody sent here by an invitation link. They may not have an account yet — and nobody signs
   * themselves up any more (D16) — so this is the one place a new account can be made from.
   */
  protected readonly invited = computed(
    () => this.returnUrl()?.startsWith('/venue-invitation') ?? false,
  );

  protected readonly submitting = signal(false);
  protected readonly formError = signal<string | null>(null);

  protected submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.submitting()) {
      return;
    }

    this.submitting.set(true);
    this.formError.set(null);
    const { email, password } = this.form.getRawValue();

    const door = this.door();

    // Accepting an invitation is how somebody becomes staff, so the door cannot ask them to be
    // staff already; and a page the guard interrupted checks its own permission at the server.
    if (door === null || this.returnUrl()) {
      this.auth.login(email, password).subscribe({
        next: () => {
          this.submitting.set(false);
          void this.router.navigateByUrl(safeReturnUrl(this.returnUrl()));
        },
        error: (error: unknown) => this.failed(error),
      });
      return;
    }

    // Which door is theirs is read from the roles the server gives for each venue (D15).
    this.auth
      .login(email, password)
      .pipe(switchMap((user) => this.venues.mine().pipe(map((venues) => ({ user, venues })))))
      .subscribe({
        next: ({ user, venues }) => {
          const refused = refusal(door, venues, user.isPlatformAdmin);
          if (refused) {
            // Signed out again rather than left inside behind the wrong door: the next thing they
            // do should be the other door, not a page that was never meant for them.
            this.auth.logout().subscribe(() => this.failed(null, refused));
            return;
          }

          this.submitting.set(false);
          void this.router.navigateByUrl(landing(door, venues, user.isPlatformAdmin));
        },
        error: (error: unknown) => this.failed(error),
      });
  }

  private failed(error: unknown, key?: string): void {
    this.submitting.set(false);
    this.formError.set(key ?? errorKey(error));
  }
}

/** Only a path inside this app: "//evil.com" is a URL to somewhere else, not a route here. */
function safeReturnUrl(candidate: string | undefined): string {
  return candidate?.startsWith('/') && !candidate.startsWith('//') ? candidate : '/';
}
