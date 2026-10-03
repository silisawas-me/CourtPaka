import { Component, computed, inject, input, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Router, RouterLink } from '@angular/router';
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
  private readonly router = inject(Router);

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
  /** Where the login page was sent back to: a staff invitation link, when there is one. */
  readonly returnUrl = input<string>();

  /**
   * The staff invitation this sign-up came from (thai-fit T1), read from the link the owner sent
   * over LINE. With it, a phone number will do instead of an address.
   */
  protected readonly link = computed(() => invitationIn(this.returnUrl()));

  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);
  protected readonly done = signal(false);

  ngOnInit(): void {
    const invited = this.email();
    if (invited) {
      this.form.controls.email.setValue(invited);
    }
    if (this.link()) {
      // An address is optional on a link; one of the two is checked at submit.
      this.form.controls.email.setValidators(Validators.email);
      this.form.controls.email.updateValueAndValidity();
    }
    // Fetch the policy version while the form is being filled in, so submitting costs one request.
    this.auth.privacyPolicyVersion().subscribe({ error: () => undefined });
  }

  protected submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.submitting()) {
      return;
    }

    const { email, password, phoneNumber } = this.form.getRawValue();
    const link = this.link();
    if (link && !email.trim() && !phoneNumber.trim()) {
      this.errorKey.set('register.needPhoneOrEmail');
      return;
    }

    this.submitting.set(true);
    this.errorKey.set(null);

    this.auth
      .privacyPolicyVersion()
      .pipe(
        // The server owns the policy version; the page never decides what the user accepted.
        switchMap((privacyPolicyVersion) =>
          this.auth.register({
            email: email.trim() || null,
            password,
            privacyPolicyVersion,
            language: this.i18n.language(),
            phoneNumber: phoneNumber.trim() || null,
            ...(link ? { invitationId: link.invitationId, invitationToken: link.token } : {}),
          }),
        ),
      )
      .subscribe({
        next: () => {
          if (link) {
            // From a link they came to join, not to read an email: signed in, and on to the seat.
            this.joinThrough(email.trim() || phoneNumber.trim(), password);
            return;
          }
          this.submitting.set(false);
          this.done.set(true);
        },
        error: (error: unknown) => {
          this.submitting.set(false);
          this.errorKey.set(errorKey(error));
        },
      });
  }

  private joinThrough(signInAs: string, password: string): void {
    this.auth.login(signInAs, password).subscribe({
      next: () => {
        this.submitting.set(false);
        void this.router.navigateByUrl(this.returnUrl()!);
      },
      error: () => {
        // The account exists either way; the sign-in page takes them back to the link.
        this.submitting.set(false);
        this.done.set(true);
      },
    });
  }
}

/** The invitation in a `/venue-invitation?invitationId=…&token=…` path, or null. */
export function invitationIn(
  returnUrl: string | undefined,
): { invitationId: string; token: string } | null {
  if (!returnUrl?.startsWith('/venue-invitation?')) {
    return null;
  }
  const query = new URLSearchParams(returnUrl.slice(returnUrl.indexOf('?') + 1));
  const invitationId = query.get('invitationId');
  const token = query.get('token');
  return invitationId && token ? { invitationId, token } : null;
}
