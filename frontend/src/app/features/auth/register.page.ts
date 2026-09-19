import { Component, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { switchMap } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { FieldError } from '../../shared/field-error';

@Component({
  selector: 'app-register-page',
  imports: [ReactiveFormsModule, RouterLink, FieldError],
  templateUrl: './register.page.html',
})
export class RegisterPage implements OnInit {
  private readonly auth = inject(AuthService);

  protected readonly i18n = inject(TranslationService);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]],
    phoneNumber: [''],
    acceptPolicy: [false, Validators.requiredTrue],
  });

  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);
  protected readonly done = signal(false);

  ngOnInit(): void {
    // Fetch the policy version while the form is being filled in, so submitting costs one request.
    this.auth.privacyPolicyVersion().subscribe({ error: () => undefined });
  }

  protected submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.submitting()) {
      return;
    }

    this.submitting.set(true);
    this.errorKey.set(null);
    const { email, password, phoneNumber } = this.form.getRawValue();

    this.auth
      .privacyPolicyVersion()
      .pipe(
        // The server owns the policy version; the page never decides what the user accepted.
        switchMap((privacyPolicyVersion) =>
          this.auth.register({
            email,
            password,
            privacyPolicyVersion,
            language: this.i18n.language(),
            phoneNumber: phoneNumber || null,
          }),
        ),
      )
      .subscribe({
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
