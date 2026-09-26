import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { errorKey } from '../../core/http/api-error';
import { venueNow } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { HourPackage, PackagesService } from '../../core/venues/packages.service';
import { Availability, PublicVenueService } from '../../core/venues/public-venue.service';
import {
  CounterPayment,
  VenueBooking,
  VenueBookingsService,
} from '../../core/venues/venue-bookings.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

/** As much as the column holds (Booking.CustomerNameMaxLength, CustomerPhoneMaxLength). */
const NAME_MAX_LENGTH = 200;
const PHONE_MAX_LENGTH = 20;

const PAYMENTS: CounterPayment[] = ['Cash', 'Transfer'];

/** One court-hour picked on the grid. */
interface Pick {
  courtId: string;
  hour: number;
}

/**
 * Selling hours at the counter to somebody standing at it (PRD US-13).
 *
 * The grid is the booker's grid, read from the same endpoint, so the counter and the booker see
 * the same free hours and neither can take one the other already has. The one thing the counter
 * does differently is the clock: an hour that has started can still be sold, because the person
 * is already here — so only hours that are over are taken off the grid.
 *
 * Nothing here decides anything. The server prices the hours, refuses any already taken, and
 * starts the booking confirmed; this sends the picks and says what came back.
 */
@Component({
  selector: 'app-counter-booking',
  imports: [
    ReactiveFormsModule,
    FieldError,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './counter-booking.html',
  styleUrl: './counter-booking.scss',
})
export class CounterBooking {
  private readonly publicVenues = inject(PublicVenueService);
  private readonly bookings = inject(VenueBookingsService);
  private readonly packages = inject(PackagesService);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly payments = PAYMENTS;
  protected readonly nameMaxLength = NAME_MAX_LENGTH;
  protected readonly phoneMaxLength = PHONE_MAX_LENGTH;

  readonly venueId = input.required<string>();

  /** The day the counter is looking at, as the API names it. */
  readonly date = input.required<string>();

  /** Said once the booking is written, so the day around it can be read again. */
  readonly taken = output<VenueBooking>();
  readonly closed = output<void>();

  protected readonly grid = signal<Availability | null>(null);
  protected readonly loading = signal(true);
  protected readonly picks = signal<Pick[]>([]);
  protected readonly sending = signal(false);
  protected readonly error = signal<string | null>(null);

  /**
   * The packages this venue has sold that still have hours on them (PRD US-31). Loaded with the
   * grid rather than when somebody asks: the question "are they on a package?" is asked while
   * the customer is standing there, and a second round trip then is a queue.
   */
  protected readonly packagesWithHours = signal<HourPackage[]>([]);

  protected readonly form = this.forms.nonNullable.group({
    customerName: ['', [Validators.required, Validators.maxLength(NAME_MAX_LENGTH)]],
    customerPhone: ['', Validators.maxLength(PHONE_MAX_LENGTH)],
    paidBy: this.forms.nonNullable.control<CounterPayment>('Cash'),

    // Null means money. A package named here is what pays, and how they paid stops being asked.
    packageId: this.forms.control<string | null>(null),
  });

  /** Whether hours are paying for this one rather than money. */
  protected readonly onHours = computed(() => this.form.controls.packageId.value !== null);

  /** What the picks come to, from the prices the grid was drawn with. The server charges its own. */
  protected readonly total = computed(() => {
    const grid = this.grid();
    if (grid === null) {
      return 0;
    }

    return this.picks().reduce((sum, pick) => {
      const court = grid.courts.find((one) => one.courtId === pick.courtId);
      const hour = court?.hours.find((one) => one.hour === pick.hour);
      return sum + (hour?.bahtPerHour ?? 0);
    }, 0);
  });

  constructor() {
    effect(() => this.load(this.venueId(), this.date()));
  }

  /**
   * Whether an hour can be sold here and now: free on the grid, and not already over. An hour
   * that has started is fine — the person is standing at the counter (PRD US-13).
   */
  protected sellable(status: string, date: string, hour: number): boolean {
    return status === 'Free' && !this.over(date, hour);
  }

  protected picked(courtId: string, hour: number): boolean {
    return this.picks().some((pick) => pick.courtId === courtId && pick.hour === hour);
  }

  protected toggle(courtId: string, hour: number): void {
    this.error.set(null);
    this.picks.update((picks) =>
      this.picked(courtId, hour)
        ? picks.filter((pick) => !(pick.courtId === courtId && pick.hour === hour))
        : [...picks, { courtId, hour }],
    );
  }

  protected send(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.picks().length === 0 || this.sending()) {
      if (this.picks().length === 0) {
        this.error.set('counter.pickAnHour');
      }
      return;
    }

    const { customerName, customerPhone, paidBy, packageId } = this.form.getRawValue();
    this.sending.set(true);
    this.error.set(null);

    this.bookings
      .takeAtCounter(this.venueId(), {
        slots: this.picks().map((pick) => ({
          courtId: pick.courtId,
          date: this.date(),
          hour: pick.hour,
        })),
        customerName: customerName.trim(),
        customerPhone: customerPhone.trim() || null,
        paidBy: packageId === null ? paidBy : null,
        packageId,
      })
      .subscribe({
        next: (booking) => {
          this.sending.set(false);
          this.picks.set([]);
          this.form.reset({
            customerName: '',
            customerPhone: '',
            paidBy: 'Cash',
            packageId: null,
          });

          // A package that just paid for something has fewer hours on it now.
          this.loadPackages(this.venueId());
          this.taken.emit(booking);
        },
        error: (failure: unknown) => {
          this.sending.set(false);
          this.error.set(errorKey(failure));

          // Usually somebody took one of these hours a moment ago; the grid is out of date.
          this.load(this.venueId(), this.date());
        },
      });
  }

  /** An hour that has ended cannot be sold, not even at the counter. */
  private over(date: string, hour: number): boolean {
    const now = venueNow();
    return date < now.date || (date === now.date && hour + 1 <= now.hour + now.minute / 60);
  }

  /**
   * The packages this venue has sold that still have hours on them. A refusal is not put on the
   * screen: a counter that cannot read them can still take money, which is the thing it is for.
   */
  private loadPackages(venueId: string): void {
    this.packages.sold(venueId).subscribe({
      next: (sold) =>
        this.packagesWithHours.set(
          sold.filter((one) => one.hoursLeft > 0 && one.expiredAt === null),
        ),
      error: () => this.packagesWithHours.set([]),
    });
  }

  private load(venueId: string, date: string): void {
    this.loading.set(true);
    this.loadPackages(venueId);

    this.publicVenues.availability(venueId, date).subscribe({
      next: (grid) => {
        this.loading.set(false);
        this.grid.set(grid);

        // Keep only picks that are still for sale; the rest were taken while this was open.
        this.picks.update((picks) =>
          picks.filter((pick) =>
            grid.courts
              .find((court) => court.courtId === pick.courtId)
              ?.hours.some(
                (hour) => hour.hour === pick.hour && this.sellable(hour.status, date, hour.hour),
              ),
          ),
        );
      },
      error: (failure: unknown) => {
        this.loading.set(false);
        this.error.set(errorKey(failure));
      },
    });
  }
}
