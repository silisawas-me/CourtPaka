import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { HourPackage, PackagesService, PackageType } from '../../core/venues/packages.service';
import { PaymentMethod } from '../../core/venues/venue-bookings.service';

/** The three ways the dialog takes money, left to right as the design draws them. */
const PAY_WITH: readonly PaymentMethod[] = [
  'PromptPay',
  'Card',
  'Cash',
  'BankTransfer',
  'TrueMoney',
];

/** As long as the customer's name and phone may be (Booking's columns, which a sale shares). */
const NAME_MAX_LENGTH = 200;
const PHONE_MAX_LENGTH = 20;

/**
 * "เพิ่มสมาชิก · ขายแพ็กเกจ" (docs/plan/owner-complete.md, artboard a): adding a member is selling
 * somebody a package, in one dialog over the members table — the package as cards, who it is for,
 * how they paid, and the day it runs out — with one wide button that says the price.
 *
 * The server sells it (PRD US-31): the money is in today's till, the hours are on the package.
 */
@Component({
  selector: 'app-sell-package',
  imports: [AppDatePipe, BahtPipe],
  templateUrl: './sell-package.html',
  styleUrls: ['./walk-in.scss', './sell-package.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown.escape)': 'closed.emit()' },
})
export class SellPackage {
  private readonly packages = inject(PackagesService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  /** The offers on the board, as the page already has them. */
  readonly offers = input.required<readonly PackageType[]>();

  readonly sold = output<HourPackage>();
  readonly closed = output<void>();

  protected readonly payWith = PAY_WITH;
  protected readonly nameMaxLength = NAME_MAX_LENGTH;
  protected readonly phoneMaxLength = PHONE_MAX_LENGTH;

  private readonly picked = signal<string | null>(null);
  protected readonly name = signal('');
  protected readonly phone = signal('');
  protected readonly paidBy = signal<PaymentMethod>(PAY_WITH[0]);
  protected readonly tried = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  /** The chosen offer: the one pressed, or the first on the board until one is. */
  protected readonly offer = computed(
    () => this.offers().find((one) => one.typeId === this.picked()) ?? this.offers()[0] ?? null,
  );

  /** The day it runs out, counted as the server counts it: from today, the offer's days on. */
  protected readonly until = computed(() => {
    const offer = this.offer();
    if (!offer) {
      return null;
    }
    const day = venueToday();
    day.setDate(day.getDate() + offer.validForDays);
    return plainDate(day);
  });

  protected readonly nameMissing = computed(() => this.name().trim() === '');

  protected pick(offer: PackageType): void {
    this.picked.set(offer.typeId);
  }

  protected sell(): void {
    const offer = this.offer();
    this.tried.set(true);
    if (!offer || this.nameMissing() || this.busy()) {
      return;
    }

    const phone = this.phone().trim();
    this.busy.set(true);
    this.error.set(null);
    this.packages
      .sell(this.venueId(), {
        packageTypeId: offer.typeId,
        customerName: this.name().trim(),
        customerPhone: phone === '' ? null : phone,
        paidBy: this.paidBy(),
      })
      .subscribe({
        next: (bought) => {
          this.busy.set(false);
          this.sold.emit(bought);
        },
        error: (failure: unknown) => {
          this.busy.set(false);
          this.error.set(errorKey(failure));
        },
      });
  }
}
