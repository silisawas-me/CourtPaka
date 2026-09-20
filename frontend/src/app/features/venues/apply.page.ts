import { Component, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { VenueService } from '../../core/venues/venue.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

/** A Thai tax number is thirteen digits and a branch code is five (PRD 7.2). */
const TAX_ID_LENGTH = 13;
const TAX_BRANCH_LENGTH = 5;

/** The head office, which is what most venues are, so it is what the form starts on. */
const HEAD_OFFICE = '00000';

/**
 * Applying to join the platform (PRD US-10).
 *
 * Fourteen fields, but three separate subjects, and the grouping is the design: where the court
 * is, where the money lands, and who the venue is to the Revenue Department. Somebody filling
 * this in has a bank app open for one group and a company certificate open for another, so the
 * page says which is which rather than presenting one long column of boxes.
 *
 * Not a wizard. A venue applies once and then waits, and splitting one sitting across several
 * pages only hides how much is left to type.
 */
@Component({
  selector: 'app-venue-apply-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './apply.page.html',
  styleUrl: './apply.page.scss',
})
export class VenueApplyPage implements OnInit {
  private readonly venues = inject(VenueService);
  private readonly router = inject(Router);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);

  /**
   * The agreement version the platform is asking for. Read here and sent back with the
   * application, so what was on screen is what is recorded as accepted (PRD US-10, Q8).
   */
  protected readonly agreementVersion = signal<string | null>(null);

  protected readonly submitting = signal(false);
  protected readonly errorKey = signal<string | null>(null);

  protected readonly form = this.forms.nonNullable.group({
    code: ['', [Validators.required, Validators.minLength(3), Validators.maxLength(6)]],
    name: ['', Validators.required],
    addressLine: ['', Validators.required],
    district: ['', Validators.required],
    province: ['', Validators.required],

    promptPayId: ['', Validators.required],
    promptPayAccountName: ['', Validators.required],

    isVatRegistered: [false],
    legalName: ['', Validators.required],
    taxId: [
      '',
      [
        Validators.required,
        Validators.minLength(TAX_ID_LENGTH),
        Validators.maxLength(TAX_ID_LENGTH),
      ],
    ],
    taxBranch: [
      HEAD_OFFICE,
      [
        Validators.required,
        Validators.minLength(TAX_BRANCH_LENGTH),
        Validators.maxLength(TAX_BRANCH_LENGTH),
      ],
    ],
    billingAddress: ['', Validators.required],

    // Optional, and only useful as a pair: half a pin is not a place (PRD US-10).
    latitude: this.forms.control<number | null>(null),
    longitude: this.forms.control<number | null>(null),

    acceptsAgreement: [false, Validators.requiredTrue],
  });

  ngOnInit(): void {
    this.venues.agreement().subscribe({
      next: ({ version }) => this.agreementVersion.set(version),
      error: (error: unknown) => this.errorKey.set(errorKey(error)),
    });
  }

  /** The court's own address, offered as the address for documents, which it usually is. */
  protected copyAddress(): void {
    const { addressLine, district, province } = this.form.getRawValue();
    this.form.patchValue({
      billingAddress: [addressLine, district, province].filter(Boolean).join(' '),
    });
  }

  protected submit(): void {
    this.form.markAllAsTouched();
    const version = this.agreementVersion();

    if (this.form.invalid || this.submitting() || version === null) {
      return;
    }

    const value = this.form.getRawValue();
    this.submitting.set(true);
    this.errorKey.set(null);

    this.venues
      .apply({
        code: value.code.trim(),
        name: value.name.trim(),
        address: {
          addressLine: value.addressLine.trim(),
          district: value.district.trim(),
          province: value.province.trim(),
        },
        business: {
          promptPayId: value.promptPayId.trim(),
          promptPayAccountName: value.promptPayAccountName.trim(),
          isVatRegistered: value.isVatRegistered,
          legalName: value.legalName.trim(),
          taxId: value.taxId.trim(),
          taxBranch: value.taxBranch.trim(),
          billingAddress: value.billingAddress.trim(),
          latitude: value.latitude,
          longitude: value.longitude,
        },
        agreementVersion: version,
      })
      .subscribe({
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
