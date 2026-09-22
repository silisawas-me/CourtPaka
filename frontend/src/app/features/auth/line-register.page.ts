import { Component, inject, input, OnInit, signal } from '@angular/core';
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

/**
 * The last step of signing up with LINE (PRD US-01): LINE said who came back, and the account is
 * made here — once they accept the privacy policy, which LINE's own consent screen is not (PDPA).
 *
 * The phone number is asked for here rather than left to the first booking, because this is the
 * one moment the person is already filling a form in; the booking page still says so if it is
 * skipped.
 */
@Component({
  selector: 'app-line-register-page',
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
  templateUrl: './line-register.page.html',
  styleUrl: './line-register.page.scss',
})
export class LineRegisterPage implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly i18n = inject(TranslationService);

  /** Where the person was heading when they signed in. */
  readonly returnUrl = input('/');

  protected readonly form = inject(FormBuilder).nonNullable.group({
    phoneNumber: [''],
    acceptPolicy: [false, Validators.requiredTrue],
  });

  protected readonly greeting = signal<string | null>(null);
  protected readonly sharedEmail = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);

  ngOnInit(): void {
    this.auth.privacyPolicyVersion().subscribe({ error: () => undefined });

    // Who is waiting is the server's to say: it holds LINE's answer, this page never saw it.
    this.auth.linePending().subscribe({
      next: (pending) => {
        this.greeting.set(pending.name);
        this.sharedEmail.set(pending.email);
        this.loading.set(false);
      },
      error: (failure: unknown) => {
        // Usually the wait between LINE and here ran out; starting again is the way on.
        this.errorKey.set(errorKey(failure));
        this.loading.set(false);
      },
    });
  }

  protected submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.submitting()) {
      return;
    }

    this.submitting.set(true);
    this.errorKey.set(null);
    const { phoneNumber } = this.form.getRawValue();

    this.auth
      .privacyPolicyVersion()
      .pipe(
        switchMap((privacyPolicyVersion) =>
          this.auth.completeLineSignUp({
            privacyPolicyVersion,
            language: this.i18n.language(),
            phoneNumber: phoneNumber || null,
          }),
        ),
      )
      .subscribe({
        next: () => {
          this.submitting.set(false);
          void this.router.navigateByUrl(this.returnUrl());
        },
        error: (failure: unknown) => {
          this.submitting.set(false);
          this.errorKey.set(errorKey(failure));
        },
      });
  }
}
