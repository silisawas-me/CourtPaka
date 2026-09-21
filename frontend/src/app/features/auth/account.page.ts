import { Component, inject, signal } from '@angular/core';
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
import { FieldError } from '../../shared/field-error';
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
    FieldError,
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

  protected readonly deleteForm = this.forms.nonNullable.group({
    understood: [false, Validators.requiredTrue],
    password: ['', Validators.required],
  });

  protected readonly deleting = signal(false);
  protected readonly deleteError = signal<string | null>(null);

  protected deleteAccount(): void {
    this.deleteForm.markAllAsTouched();
    if (this.deleteForm.invalid || this.deleting()) {
      return;
    }

    this.deleting.set(true);
    this.deleteError.set(null);
    this.auth.deleteAccount(this.deleteForm.getRawValue().password).subscribe({
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
