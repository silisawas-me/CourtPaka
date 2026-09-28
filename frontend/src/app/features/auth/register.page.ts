import { Component, inject, input, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { RouterLink } from '@angular/router';
import { switchMap } from 'rxjs';
import { AuthService } from '../../core/auth/auth.service';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

@Component({
  selector: 'app-register-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
  ],
  providers: [FORM_FIELD_DEFAULTS],
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

  /** The address an invitation was sent to (`/register?email=…`), filled in for them. */
  readonly email = input<string>();
  /** `owner` when the platform invited them to bring a venue: they sign in at the owner's door. */
  readonly invitedAs = input<string | undefined>(undefined, { alias: 'as' });

  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);
  protected readonly done = signal(false);

  ngOnInit(): void {
    const invited = this.email();
    if (invited) {
      this.form.controls.email.setValue(invited);
    }
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
