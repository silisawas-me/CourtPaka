import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { forkJoin } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import { HourPackage, PackagesService, PackageType } from '../../core/venues/packages.service';
import { PAYMENT_METHODS } from '../../core/venues/venue-bookings.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

/** As long as an offer's name on the board may be. The customer's own is Booking's, and longer. */
const OFFER_NAME_MAX_LENGTH = 100;
const CUSTOMER_NAME_MAX_LENGTH = 200;
const CUSTOMER_PHONE_MAX_LENGTH = 20;

/**
 * Hours sold in advance (PRD US-31).
 *
 * Two things on one page, in the order a shift uses them: what the venue has sold, which is what
 * somebody at the desk looks up when a customer says "I'm on a package"; and the board it sells
 * from, which is a setting and is changed once a season.
 *
 * The number that matters on a sold row is what is left, so it is the one that is big. The ones
 * about to run out carry the colour the app uses for something waiting on a person, because that
 * is what they are: a phone call, while the hours are still worth something.
 */
@Component({
  selector: 'app-packages-page',
  imports: [
    ReactiveFormsModule,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    AppDatePipe,
    BahtPipe,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './packages.page.html',
  styleUrl: './packages.page.scss',
})
export class PackagesPage {
  private readonly packages = inject(PackagesService);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly methods = PAYMENT_METHODS;
  protected readonly nameMaxLength = OFFER_NAME_MAX_LENGTH;

  readonly venueId = input.required<string>();

  protected readonly board$ = signal<PackageType[]>([]);
  protected readonly sold = signal<HourPackage[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  protected readonly saving = signal(false);
  protected readonly saveError = signal<string | null>(null);

  /** Whether the board is open for editing. Shut by default: it is a setting, not a shift. */
  protected readonly editingBoard = signal(false);

  protected readonly onSale = computed(() =>
    this.board$().filter((one) => one.withdrawnAt === null),
  );

  protected readonly withdrawn = computed(() =>
    this.board$().filter((one) => one.withdrawnAt !== null),
  );

  /** The ones somebody should ring, first. Everything else in the order it was sold. */
  protected readonly rows = computed(() =>
    [...this.sold()].sort((one, other) => Number(other.runningOut) - Number(one.runningOut)),
  );

  protected readonly runningOut = computed(
    () => this.sold().filter((one) => one.runningOut).length,
  );

  protected readonly hoursOwed = computed(() =>
    this.sold().reduce((total, one) => total + Math.max(0, one.hoursLeft), 0),
  );

  protected readonly nothingSold = computed(() => !this.loading() && this.sold().length === 0);

  /** Selling one to somebody at the desk. */
  protected readonly sale = this.forms.group({
    packageTypeId: this.forms.control<string | null>(null, Validators.required),
    customerName: this.forms.nonNullable.control('', [
      Validators.required,
      Validators.maxLength(CUSTOMER_NAME_MAX_LENGTH),
    ]),
    customerPhone: this.forms.nonNullable.control(
      '',
      Validators.maxLength(CUSTOMER_PHONE_MAX_LENGTH),
    ),
    paidBy: this.forms.nonNullable.control<string>(PAYMENT_METHODS[0], Validators.required),
  });

  /** Putting an offer on the board. */
  protected readonly offer = this.forms.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(OFFER_NAME_MAX_LENGTH)]],
    hours: [10, [Validators.required, Validators.min(1)]],
    priceBaht: [1800, [Validators.required, Validators.min(1)]],
    validForDays: [90, [Validators.required, Validators.min(1)]],
  });

  constructor() {
    effect(() => this.load(this.venueId()));
  }

  protected sell(): void {
    this.sale.markAllAsTouched();
    if (this.sale.invalid || this.saving()) {
      return;
    }

    const { packageTypeId, customerName, customerPhone, paidBy } = this.sale.getRawValue();

    this.saving.set(true);
    this.saveError.set(null);

    this.packages
      .sell(this.venueId(), {
        packageTypeId: packageTypeId!,
        customerName: customerName.trim(),
        customerPhone: customerPhone.trim() === '' ? null : customerPhone.trim(),
        paidBy,
      })
      .subscribe({
        next: (bought) => {
          this.saving.set(false);
          this.sold.update((all) => [bought, ...all]);
          this.sale.patchValue({ customerName: '', customerPhone: '' });
          this.sale.controls.customerName.markAsUntouched();
        },
        error: (failure: unknown) => this.refused(failure),
      });
  }

  protected putOnTheBoard(): void {
    this.offer.markAllAsTouched();
    if (this.offer.invalid || this.saving()) {
      return;
    }

    const asked = this.offer.getRawValue();

    this.saving.set(true);
    this.saveError.set(null);

    this.packages.offer(this.venueId(), { ...asked, name: asked.name.trim() }).subscribe({
      next: (added) => {
        this.saving.set(false);
        this.board$.update((all) => [...all, added]);
        this.offer.patchValue({ name: '' });
        this.offer.controls.name.markAsUntouched();
        this.pickIfOnlyOne();
      },
      error: (failure: unknown) => this.refused(failure),
    });
  }

  protected withdraw(one: PackageType): void {
    if (this.saving()) {
      return;
    }

    this.saving.set(true);
    this.saveError.set(null);

    this.packages.withdraw(this.venueId(), one.typeId).subscribe({
      next: (gone) => {
        this.saving.set(false);
        this.board$.update((all) => all.map((row) => (row.typeId === gone.typeId ? gone : row)));

        if (this.sale.value.packageTypeId === gone.typeId) {
          this.sale.patchValue({ packageTypeId: null });
        }
      },
      error: (failure: unknown) => this.refused(failure),
    });
  }

  private refused(failure: unknown): void {
    this.saving.set(false);
    this.saveError.set(errorKey(failure));
  }

  private pickIfOnlyOne(): void {
    const only = this.onSale();
    if (only.length === 1) {
      this.sale.patchValue({ packageTypeId: only[0].typeId });
    }
  }

  private load(venueId: string): void {
    this.loading.set(true);
    this.pageError.set(null);

    forkJoin({
      board: this.packages.board(venueId),
      sold: this.packages.sold(venueId),
    }).subscribe({
      next: ({ board, sold }) => {
        this.loading.set(false);
        this.board$.set(board);
        this.sold.set(sold);
        this.pickIfOnlyOne();

        // Nothing to sell from yet, so the thing to do first is put something on the board.
        this.editingBoard.set(this.onSale().length === 0);
      },
      error: (failure: unknown) => {
        this.loading.set(false);
        this.pageError.set(errorKey(failure));
      },
    });
  }
}
