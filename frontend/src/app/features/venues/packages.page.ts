import { Router } from '@angular/router';
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
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { SellPackage } from './sell-package';

/** As long as an offer's name on the board may be. The customer's own is Booking's, and longer. */
const OFFER_NAME_MAX_LENGTH = 100;

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
    SellPackage,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './packages.page.html',
  styleUrl: './packages.page.scss',
})
export class PackagesPage {
  private readonly packages = inject(PackagesService);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly nameMaxLength = OFFER_NAME_MAX_LENGTH;

  readonly venueId = input.required<string>();
  /**
   * The page under "อื่น ๆ" (route data): the board of offers and each package's hours. Without
   * it this is the design's members section — search, filters, "+ เพิ่มสมาชิก" and the table.
   */
  readonly detail = input(false);
  private readonly router = inject(Router);

  protected readonly board$ = signal<PackageType[]>([]);
  protected readonly sold = signal<HourPackage[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  protected readonly saving = signal(false);
  protected readonly saveError = signal<string | null>(null);

  /** The sell-package dialog is open ("+ เพิ่มสมาชิก", artboard a). */
  protected readonly selling = signal(false);

  /** A package sold in the dialog is a member at the top of the table. */
  protected added(bought: HourPackage): void {
    this.sold.update((all) => [bought, ...all]);
    this.selling.set(false);
  }

  /** Nothing to sell yet: close the dialog and open the board to put an offer on it. */
  protected toBoard(): void {
    this.selling.set(false);
    if (!this.detail()) {
      void this.router.navigate(['/venues', this.venueId(), 'package-board']);
      return;
    }
    this.editingBoard.set(true);
    requestAnimationFrame(() =>
      document.querySelector('[data-testid=offer-name]')?.scrollIntoView({ block: 'center' }),
    );
  }

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

  /** The members table's search and filter (owner app PR-6). */
  protected readonly query = signal('');
  protected readonly filters = ['all', 'live', 'runningOut'] as const;
  protected readonly filter = signal<'all' | 'live' | 'runningOut'>('all');

  /** The rows the table shows: found by name or phone, narrowed by how they stand. */
  protected readonly shown = computed(() => {
    const query = this.query().trim().toLowerCase();
    const filter = this.filter();
    return this.rows().filter(
      (one) =>
        (query === '' ||
          one.customerName.toLowerCase().includes(query) ||
          (one.customerPhone ?? '').includes(query)) &&
        (filter === 'all' ||
          (filter === 'live' && one.live) ||
          (filter === 'runningOut' && one.live && one.runningOut)),
    );
  });

  /** How a package stands, in the words the design uses — from the server's own flags. */
  protected standing(one: HourPackage): 'live' | 'runningOut' | 'done' {
    return !one.live ? 'done' : one.runningOut ? 'runningOut' : 'live';
  }

  protected readonly runningOut = computed(
    () => this.sold().filter((one) => one.runningOut).length,
  );

  protected readonly hoursOwed = computed(() =>
    this.sold().reduce((total, one) => total + Math.max(0, one.hoursLeft), 0),
  );

  protected readonly nothingSold = computed(() => !this.loading() && this.sold().length === 0);

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
      },
      error: (failure: unknown) => this.refused(failure),
    });
  }

  private refused(failure: unknown): void {
    this.saving.set(false);
    this.saveError.set(errorKey(failure));
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
