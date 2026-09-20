import { Component, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconButton } from '@angular/material/button';
import { MatInputModule } from '@angular/material/input';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { Alpaca } from '../../shared/alpaca';
import { CourtArt } from '../../shared/court-art';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

@Component({
  selector: 'app-login-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    FieldError,
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

  protected readonly i18n = inject(TranslationService);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
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

    this.auth.login(email, password).subscribe({
      next: () => {
        this.submitting.set(false);
        // Come back to whatever the guard interrupted, or home when the user came here directly.
        void this.router.navigateByUrl(safeReturnUrl(this.returnUrl()));
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        this.formError.set(errorKey(error));
      },
    });
  }
}

/** Only a path inside this app: "//evil.com" is a URL to somewhere else, not a route here. */
function safeReturnUrl(candidate: string | undefined): string {
  return candidate?.startsWith('/') && !candidate.startsWith('//') ? candidate : '/';
}
