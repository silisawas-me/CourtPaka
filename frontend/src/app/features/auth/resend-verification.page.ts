import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

@Component({
  selector: 'app-resend-verification-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './resend-verification.page.html',
})
export class ResendVerificationPage {
  private readonly auth = inject(AuthService);

  protected readonly i18n = inject(TranslationService);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
  });

  protected readonly submitting = signal(false);
  protected readonly done = signal(false);
  protected readonly errorKey = signal<string | null>(null);

  protected submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.submitting()) {
      return;
    }

    this.submitting.set(true);
    this.errorKey.set(null);

    this.auth.resendVerification(this.form.getRawValue().email).subscribe({
      next: () => {
        this.submitting.set(false);
        this.done.set(true);
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        this.errorKey.set(errorKey(error));
      },
    });
  }
}
