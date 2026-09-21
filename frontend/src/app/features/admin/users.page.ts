import { Component, inject, signal } from '@angular/core';
import { FormBuilder, FormGroupDirective, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { AdminService, AdminUser, AdminUserDetail } from '../../core/admin/admin.service';
import { errorKey } from '../../core/http/api-error';
import { AppDateTimePipe } from '../../core/i18n/app-date.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { AdminTabs } from './admin-tabs';

/** AccountStatusChange.ReasonMaxLength. */
const REASON_MAX_LENGTH = 500;

/**
 * The platform finding an account and stopping it, or letting it back in (PRD US-22).
 *
 * Search, then open one account in place; its history and the one door that applies to it sit
 * together, and the door asks why before it does anything — a suspension is a decision about a
 * person, and it is asked about later.
 */
@Component({
  selector: 'app-admin-users-page',
  imports: [
    AdminTabs,
    ReactiveFormsModule,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    AppDateTimePipe,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './users.page.html',
  styleUrl: './users.page.scss',
})
export class AdminUsersPage {
  private readonly admin = inject(AdminService);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly reasonMaxLength = REASON_MAX_LENGTH;

  protected readonly searchForm = this.forms.nonNullable.group({
    // How much of an address is enough is the server's rule (admin.query_too_short); the page
    // says so from the code it answers with rather than keeping a copy of the number.
    query: ['', Validators.required],
  });

  protected readonly reasonForm = this.forms.nonNullable.group({
    reason: ['', [Validators.required, Validators.maxLength(REASON_MAX_LENGTH)]],
  });

  protected readonly results = signal<AdminUser[] | null>(null);
  protected readonly searching = signal(false);
  protected readonly searchError = signal<string | null>(null);

  protected readonly open = signal<AdminUserDetail | null>(null);
  protected readonly deciding = signal(false);
  protected readonly decideError = signal<string | null>(null);

  protected search(): void {
    this.searchForm.markAllAsTouched();
    if (this.searchForm.invalid) {
      return;
    }

    this.searching.set(true);
    this.searchError.set(null);
    this.open.set(null);

    this.admin.searchUsers(this.searchForm.getRawValue().query.trim()).subscribe({
      next: (users) => {
        this.searching.set(false);
        this.results.set(users);
      },
      error: (failure: unknown) => {
        this.searching.set(false);
        this.searchError.set(errorKey(failure));
      },
    });
  }

  protected show(user: AdminUser): void {
    if (this.open()?.user.id === user.id) {
      this.open.set(null);
      return;
    }

    this.decideError.set(null);
    this.reasonForm.reset();
    this.admin.user(user.id).subscribe({
      next: (detail) => this.open.set(detail),
      error: (failure: unknown) => this.decideError.set(errorKey(failure)),
    });
  }

  /** Suspends an account that may sign in, or lets a suspended one back in. */
  protected decide(detail: AdminUserDetail, form: FormGroupDirective): void {
    this.reasonForm.markAllAsTouched();
    if (this.reasonForm.invalid || this.deciding()) {
      return;
    }

    this.deciding.set(true);
    this.decideError.set(null);

    const suspend = detail.user.suspendedAt === null;
    this.admin
      .setStanding(detail.user.id, suspend, this.reasonForm.getRawValue().reason.trim())
      .subscribe({
        next: (after) => {
          this.deciding.set(false);
          this.open.set(after);
          // Through the directive, not the group: the group forgets what was typed, but only
          // the directive forgets it was submitted, and a submitted empty field reads as an error.
          form.resetForm();
          this.results.update(
            (list) => list?.map((one) => (one.id === after.user.id ? after.user : one)) ?? null,
          );
        },
        error: (failure: unknown) => {
          this.deciding.set(false);
          this.decideError.set(errorKey(failure));
        },
      });
  }
}
