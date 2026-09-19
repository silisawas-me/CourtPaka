import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiError, AuthService } from '../../core/auth/auth.service';
import { TranslationService } from '../../core/i18n/translation.service';
import { TranslatePipe } from '../../core/i18n/translate.pipe';

@Component({
  selector: 'app-register-page',
  imports: [ReactiveFormsModule, RouterLink, TranslatePipe],
  templateUrl: './register.page.html',
})
export class RegisterPage {
  private readonly auth = inject(AuthService);
  private readonly translations = inject(TranslationService);
  private readonly router = inject(Router);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]],
    phoneNumber: [''],
    acceptPolicy: [false, Validators.requiredTrue],
  });

  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);
  protected readonly done = signal(false);

  protected submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.submitting()) {
      return;
    }

    this.submitting.set(true);
    this.errorKey.set(null);
    const { email, password, phoneNumber } = this.form.getRawValue();

    // The server owns the policy version, so read it at submit time instead of trusting the page.
    this.auth.privacyPolicyVersion().subscribe({
      next: (privacyPolicyVersion) =>
        this.auth
          .register({
            email,
            password,
            privacyPolicyVersion,
            language: this.translations.language(),
            phoneNumber: phoneNumber || null,
          })
          .subscribe({
            next: () => {
              this.submitting.set(false);
              this.done.set(true);
            },
            error: (error: unknown) => this.fail(error),
          }),
      error: (error: unknown) => this.fail(error),
    });
  }

  protected goToLogin(): void {
    void this.router.navigate(['/login']);
  }

  private fail(error: unknown): void {
    this.submitting.set(false);
    this.errorKey.set(`error.${error instanceof ApiError ? error.code : 'unknown'}`);
  }
}
