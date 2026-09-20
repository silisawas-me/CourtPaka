import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { courtsOf, hoursOf } from '../../core/bookings/hours';
import { errorKey } from '../../core/http/api-error';
import { plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import {
  CancellationReason,
  VenueBooking,
  VenueBookingsService,
} from '../../core/venues/venue-bookings.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { provideLocalizedDateAdapter } from '../../shared/localized-date-adapter';

/** The three answers PRD 6.1 will accept for turning a paid booking away. */
const REASONS: CancellationReason[] = ['CustomerRequest', 'VenueInitiated', 'PaymentNotReceived'];

/** As much as the venue may write against a booking, and as much as the column holds. */
const NOTE_MAX_LENGTH = 400;

/** Which question a row is being asked. One row at a time: these decide money. */
type Asking = 'cancel' | 'noShow' | 'settle' | 'played';

/**
 * A day at the counter (PRD US-13): who booked what, and every door the venue can press on it.
 *
 * Denser than the booker's own list on purpose — this is read across a counter while somebody
 * waits, so it is rows in the order they are played rather than cards. What a door would do to
 * the money is shown before it is pressed, and the answer comes from the server, which is also
 * what decides it.
 */
@Component({
  selector: 'app-venue-bookings-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
  ],
  providers: [FORM_FIELD_DEFAULTS, provideLocalizedDateAdapter()],
  templateUrl: './venue-bookings.page.html',
  styleUrl: './venue-bookings.page.scss',
})
export class VenueBookingsPage {
  private readonly bookings = inject(VenueBookingsService);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly reasons = REASONS;
  protected readonly noteMaxLength = NOTE_MAX_LENGTH;

  readonly venueId = input.required<string>();

  /** The day being looked at. The counter's own day, not the reader's (PRD BR-10). */
  protected readonly day = signal(plainDate(venueToday()));
  protected readonly dayField = new FormControl(venueToday());

  protected readonly bookings$ = signal<VenueBooking[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  /** Which booking is being asked about, and which question. */
  protected readonly asking = signal<{ bookingId: string; door: Asking } | null>(null);
  protected readonly deciding = signal(false);
  protected readonly decideError = signal<string | null>(null);

  protected readonly nothingToday = computed(
    () => !this.loading() && this.bookings$().length === 0,
  );

  /** Turning a paid booking away needs one of three reasons, and may carry a note (PRD 6.1). */
  protected readonly cancelForm = this.forms.group({
    reason: this.forms.control<CancellationReason | null>(null),
    note: this.forms.nonNullable.control('', Validators.maxLength(NOTE_MAX_LENGTH)),
  });

  /** Taking a no-show back says the record is wrong, so it has to say why (PRD 6.1). */
  protected readonly playedForm = this.forms.group({
    reason: this.forms.nonNullable.control('', [
      Validators.required,
      Validators.maxLength(NOTE_MAX_LENGTH),
    ]),
  });

  constructor() {
    // The day and the venue both come from outside, and the venue only after the first pass, so
    // reading them here is what waits for both.
    effect(() => this.load(this.venueId(), this.day()));
  }

  protected pick(chosen: Date | null): void {
    if (chosen === null) {
      return;
    }

    this.close();
    this.day.set(plainDate(chosen));
  }

  protected ask(bookingId: string, door: Asking): void {
    this.asking.set({ bookingId, door });
    this.decideError.set(null);
    this.cancelForm.reset({ reason: null, note: '' });
    this.playedForm.reset({ reason: '' });
  }

  protected asked(bookingId: string, door: Asking): boolean {
    const asking = this.asking();
    return asking?.bookingId === bookingId && asking.door === door;
  }

  protected close(): void {
    this.asking.set(null);
    this.decideError.set(null);
  }

  /**
   * Turning a booking away. A booking still being checked asks about the money instead of a
   * reason, because the venue is the one who can see whether it arrived (PRD 6.1).
   */
  protected cancel(booking: VenueBooking, paymentReceived?: boolean): void {
    if (booking.status === 'PendingVerification') {
      this.send(
        booking,
        this.bookings.cancel(this.venueId(), booking.bookingId, {
          paymentReceived,
        }),
      );
      return;
    }

    const { reason, note } = this.cancelForm.getRawValue();
    if (reason === null) {
      this.decideError.set('error.booking.reason_required');
      return;
    }

    this.send(
      booking,
      this.bookings.cancel(this.venueId(), booking.bookingId, {
        reason,
        note: note.trim() || undefined,
      }),
    );
  }

  protected noShow(booking: VenueBooking): void {
    this.send(booking, this.bookings.noShow(this.venueId(), booking.bookingId));
  }

  protected settle(booking: VenueBooking, paymentReceived: boolean): void {
    this.send(
      booking,
      this.bookings.settlePayment(this.venueId(), booking.bookingId, paymentReceived),
    );
  }

  protected playedAfterAll(booking: VenueBooking): void {
    this.playedForm.markAllAsTouched();
    if (this.playedForm.invalid) {
      return;
    }

    this.send(
      booking,
      this.bookings.playedAfterAll(
        this.venueId(),
        booking.bookingId,
        this.playedForm.getRawValue().reason.trim(),
      ),
    );
  }

  protected hours(booking: VenueBooking): string {
    return hoursOf(booking.slots);
  }

  protected courts(booking: VenueBooking): string {
    return courtsOf(booking.slots);
  }

  private send(booking: VenueBooking, decision: Observable<VenueBooking>): void {
    if (this.deciding()) {
      return;
    }

    this.deciding.set(true);
    this.decideError.set(null);

    decision.subscribe({
      next: (decided) => {
        this.deciding.set(false);
        this.close();
        // The row is replaced with what the server now says about it, rather than with what the
        // page hoped: the doors that are open change with every decision.
        this.bookings$.update((day) =>
          day.map((row) => (row.bookingId === decided.bookingId ? decided : row)),
        );
      },
      error: (failure: unknown) => {
        this.deciding.set(false);
        this.decideError.set(errorKey(failure));
        // Usually somebody else decided first, so what is on screen is already out of date.
        this.load(this.venueId(), this.day(), { quiet: true });
      },
    });
  }

  private load(venueId: string, day: string, { quiet = false } = {}): void {
    this.loading.set(!quiet);
    this.pageError.set(null);

    this.bookings.day(venueId, day).subscribe({
      next: (day) => {
        this.bookings$.set(day);
        this.loading.set(false);
      },
      error: (failure: unknown) => {
        if (!quiet) {
          this.bookings$.set([]);
          this.pageError.set(errorKey(failure));
        }
        this.loading.set(false);
      },
    });
  }
}
