import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

/**
 * The signed-in person's own account, and the way to be forgotten (PDPA, PRD 8, S-15).
 *
 * Deleting is spelled out before it is offered — what goes, what stays with the venues, and what
 * has to be settled first — because it cannot be undone, and the server's refusals (a venue still
 * owned, hours still to play, money still owed) are each said in words rather than as a failure.
 */
@Component({
  selector: 'app-account-page',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatCardModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './account.page.html',
  styleUrl: './account.page.scss',
})
export class AccountPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly user = this.auth.currentUser;

  /** What came back in the address from LINE: "confirmed", or why it did not (PRD US-01). */
  readonly line = input<string>();

  protected readonly lineConfirmed = computed(() => this.line() === 'confirmed');
  protected readonly lineError = computed(() =>
    this.line() && !this.lineConfirmed() ? this.line()! : null,
  );

  /** An account with no password (LINE) proves who it is at LINE instead. */
  protected readonly confirmsWithLine = computed(() => this.user()?.hasPassword === false);

  protected readonly phoneForm = this.forms.nonNullable.group({
    phoneNumber: [''],
  });

  protected readonly savingPhone = signal(false);
  protected readonly phoneSaved = signal(false);
  protected readonly phoneError = signal<string | null>(null);

  constructor() {
    // The field starts at what the account has, and follows it when the account is read again.
    effect(() => {
      const phone = this.user()?.phoneNumber ?? '';
      untracked(() => {
        if (!this.phoneForm.controls.phoneNumber.dirty) {
          this.phoneForm.controls.phoneNumber.setValue(phone);
        }
      });
    });
  }

  protected savePhone(): void {
    if (this.savingPhone()) {
      return;
    }

    this.savingPhone.set(true);
    this.phoneSaved.set(false);
    this.phoneError.set(null);
    this.auth.changePhone(this.phoneForm.getRawValue().phoneNumber || null).subscribe({
      next: () => {
        this.savingPhone.set(false);
        this.phoneSaved.set(true);
        this.phoneForm.controls.phoneNumber.markAsPristine();
      },
      error: (failure: unknown) => {
        this.savingPhone.set(false);
        this.phoneError.set(errorKey(failure));
      },
    });
  }

  protected readonly deleteForm = this.forms.nonNullable.group({
    understood: [false, Validators.requiredTrue],
    password: [''],
  });

  protected readonly deleting = signal(false);
  protected readonly deleteError = signal<string | null>(null);

  protected deleteAccount(): void {
    this.deleteForm.markAllAsTouched();
    const { understood, password } = this.deleteForm.getRawValue();
    if (!understood || this.deleting() || (!this.confirmsWithLine() && !password)) {
      return;
    }

    this.deleting.set(true);
    this.deleteError.set(null);
    this.auth.deleteAccount(this.confirmsWithLine() ? null : password).subscribe({
      next: () => {
        this.deleting.set(false);
        void this.router.navigate(['/'], { queryParams: { deleted: 1 } });
      },
      error: (failure: unknown) => {
        this.deleting.set(false);
        this.deleteForm.controls.password.reset();
        this.deleteError.set(errorKey(failure));
      },
    });
  }
}
