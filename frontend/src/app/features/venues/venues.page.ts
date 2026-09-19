import { Component, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { Venue, VenueService } from '../../core/venues/venue.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

@Component({
  selector: 'app-venues-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './venues.page.html',
})
export class VenuesPage implements OnInit {
  private readonly venues = inject(VenueService);
  private readonly router = inject(Router);

  protected readonly i18n = inject(TranslationService);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    code: ['', [Validators.required, Validators.minLength(3), Validators.maxLength(6)]],
    name: ['', Validators.required],
    addressLine: ['', Validators.required],
    district: ['', Validators.required],
    province: ['', Validators.required],
  });

  protected readonly mine = signal<Venue[]>([]);
  protected readonly loading = signal(true);
  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);

  ngOnInit(): void {
    this.venues.mine().subscribe({
      next: (venues) => {
        this.mine.set(venues);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.errorKey.set(errorKey(error));
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
    const { code, name, ...address } = this.form.getRawValue();

    this.venues.create(code, name, address).subscribe({
      next: (venue) => {
        this.submitting.set(false);
        void this.router.navigate(['/venues', venue.id]);
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        this.errorKey.set(errorKey(error));
      },
    });
  }
}
