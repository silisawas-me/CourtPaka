import { Component, computed, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Observable } from 'rxjs';
import { courtsOf, hoursOf } from '../../core/bookings/hours';
import { VenueWaitlistEntry, WaitlistService } from '../../core/bookings/waitlist.service';
import { AppDatePipe, AppDateTimePipe } from '../../core/i18n/app-date.pipe';
import { errorKey } from '../../core/http/api-error';
import { plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import {
  CancellationReason,
  PaymentMethod,
  RefundMethod,
  Refunds,
  VenueBooking,
  VenueBookingsService,
} from '../../core/venues/venue-bookings.service';
import { FieldError } from '../../shared/field-error';
import { bookingTone, StatusChip } from '../../shared/status-chip';
import { Availability, PublicVenueService } from '../../core/venues/public-venue.service';
import { CounterBooking } from './counter-booking';
import { DayBoard } from './day-board';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { provideLocalizedDateAdapter } from '../../shared/localized-date-adapter';

/** As much as the venue may write against a booking, and as much as the column holds. */
const NOTE_MAX_LENGTH = 400;

/** Which question a row is being asked. One row at a time: these decide money. */
type Asking = 'cancel' | 'noShow' | 'settle' | 'played' | 'refund' | 'take';

/** The two ways a venue gets money back to somebody (PRD US-18). */
const REFUND_METHODS: RefundMethod[] = ['Transfer', 'Cash'];

/** The three ways money reaches the counter (PRD US-26). */
const PAYMENT_METHODS: PaymentMethod[] = ['Cash', 'PromptPay', 'Card'];

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
    CounterBooking,
    DayBoard,
    StatusChip,
    ReactiveFormsModule,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    AppDatePipe,
    AppDateTimePipe,
  ],
  providers: [FORM_FIELD_DEFAULTS, provideLocalizedDateAdapter()],
  templateUrl: './venue-bookings.page.html',
  styleUrl: './venue-bookings.page.scss',
})
export class VenueBookingsPage {
  private readonly bookings = inject(VenueBookingsService);
  private readonly venues = inject(PublicVenueService);
  private readonly destroyed = inject(DestroyRef);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly noteMaxLength = NOTE_MAX_LENGTH;

  /** The colour a status carries, from the one place that decides it (PRD US-25). */
  protected readonly bookingTone = bookingTone;
  protected readonly refundMethods = REFUND_METHODS;
  protected readonly paymentMethods = PAYMENT_METHODS;

  /** A transfer cannot have happened tomorrow, so the calendar does not offer it (PRD US-18). */
  protected readonly today = venueToday();

  readonly venueId = input.required<string>();

  /** The day being looked at. The counter's own day, not the reader's (PRD BR-10). */
  protected readonly day = signal(plainDate(venueToday()));

  /** Whether the counter is selling hours right now (PRD US-13). */
  protected readonly selling = signal(false);
  protected readonly dayField = new FormControl(venueToday());

  protected readonly bookings$ = signal<VenueBooking[]>([]);

  /**
   * The hours this venue sells on the day being looked at, which is what the board is drawn on.
   * The same answer the booker's grid is drawn from — a counter and a booker are looking at one
   * floor (PRD US-02, US-25).
   */
  protected readonly grid = signal<Availability | null>(null);

  /**
   * Who wanted hours on this day and did not get them (PRD US-27). It sits beside the day
   * because the moment it is worth reading is the moment a booking on this day is cancelled.
   */
  private readonly waitlist = inject(WaitlistService);
  protected readonly waiting = signal<VenueWaitlistEntry[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  /** Which booking is being asked about, and which question. */
  protected readonly asking = signal<{ bookingId: string; door: Asking } | null>(null);
  protected readonly deciding = signal(false);
  protected readonly decideError = signal<string | null>(null);

  protected readonly nothingToday = computed(
    () => !this.loading() && this.bookings$().length === 0,
  );

  /** Now, but only while the day on screen is today: on any other day a live line is a lie. */
  protected readonly liveNow = computed(() =>
    this.day() === plainDate(venueToday()) ? this.clock() : null,
  );

  /** Ticked every minute so the line and the counts move without the page being reloaded. */
  private readonly clock = signal(new Date());

  protected readonly todayCount = computed(() => this.bookings$().length);

  /**
   * What the day took, counted from what was actually written down as taken rather than from the
   * price of everything marked paid — so this number and the day's money page are the same number
   * (PRD US-26).
   */
  protected readonly todayTaken = computed(() =>
    this.bookings$().reduce((sum, booking) => sum + booking.takenBaht, 0),
  );

  /**
   * What is still to come in: what each booking still owes, plus money owed back and not sent.
   * A booking owes nothing forwards once the venue has turned it away, and which those are is
   * the server's answer (`can.takeMoney`), not a status list kept here.
   */
  protected readonly todayOwed = computed(() =>
    this.bookings$().reduce(
      (sum, booking) =>
        sum + booking.outstandingBaht + (booking.can.takeMoney ? booking.toPayBaht : 0),
      0,
    ),
  );

  /**
   * How much of the floor was sold, as hours out of the hours there were to sell. Counted from
   * the same grid the board is drawn on, so a court taken off sale is not counted as unsold
   * (PRD US-11, US-15's rule, for one day).
   */
  protected readonly todayUsed = computed(() => {
    const grid = this.grid();
    if (!grid) {
      return '—';
    }

    const sellable = grid.courts.reduce(
      (count, court) => count + court.hours.filter((hour) => hour.status !== 'Closed').length,
      0,
    );
    if (sellable === 0) {
      return '—';
    }

    const sold = new Set(
      this.bookings$()
        .filter((booking) => booking.status !== 'Cancelled' && booking.status !== 'Expired')
        .flatMap((booking) => booking.slots.map((slot) => `${slot.courtId}@${slot.hour}`)),
    ).size;

    return `${Math.round((sold / sellable) * 100)}%`;
  });

  /** Turning a paid booking away needs one of three reasons, and may carry a note (PRD 6.1). */
  protected readonly cancelForm = this.forms.group({
    reason: this.forms.control<CancellationReason | null>(null),
    note: this.forms.nonNullable.control('', Validators.maxLength(NOTE_MAX_LENGTH)),
  });

  /**
   * What has been sent back on the booking whose refunds are open, once it has been asked for.
   * Only one row can be open at a time, so one holder is enough.
   */
  protected readonly refunds = signal<Refunds | null>(null);

  /**
   * The record whose reason is being typed, if any. Taking money back writes into a row that can
   * never be corrected, so the reason is asked for beside the record it belongs to rather than
   * once for the list.
   */
  protected readonly voiding = signal<string | null>(null);

  protected readonly voidForm = this.forms.group({
    reason: this.forms.nonNullable.control('', [
      Validators.required,
      Validators.maxLength(NOTE_MAX_LENGTH),
    ]),
  });

  /**
   * Writing down a transfer that has already been made (PRD US-18). The amount is filled in with
   * what is still owed, because sending all of it is what usually happens and typing it again is
   * only a chance to type it wrong.
   */
  protected readonly refundForm = this.forms.group({
    amountBaht: this.forms.control<number | null>(null, [
      Validators.required,
      Validators.min(0.01),
    ]),
    refundedOn: this.forms.nonNullable.control(venueToday(), Validators.required),
    method: this.forms.nonNullable.control<RefundMethod>('Transfer'),
    note: this.forms.nonNullable.control('', Validators.maxLength(NOTE_MAX_LENGTH)),
  });

  /**
   * Taking money at the desk (PRD US-26). The amount is filled in with what is still owed,
   * because that is what usually changes hands, and the server refuses more than that anyway.
   */
  protected readonly takeForm = this.forms.group({
    amountBaht: this.forms.control<number | null>(null, [
      Validators.required,
      Validators.min(0.01),
    ]),
    method: this.forms.nonNullable.control<PaymentMethod>('Cash'),
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

    // The live line and the counts move with the clock rather than with a reload. A minute is
    // as fine as the board is drawn: an hour is a column, so a second-by-second line would move
    // less than a pixel and cost a change detection every second.
    const tick = setInterval(() => this.clock.set(new Date()), 60_000);
    this.destroyed.onDestroy(() => clearInterval(tick));
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

    if (door === 'take') {
      const owed = this.bookings$().find((row) => row.bookingId === bookingId)?.toPayBaht ?? null;
      this.takeForm.reset({ amountBaht: owed, method: 'Cash', note: '' });
    }
    this.cancelForm.reset({ reason: null, note: '' });
    this.playedForm.reset({ reason: '' });
    this.refunds.set(null);
    this.voiding.set(null);

    if (door === 'refund') {
      this.openRefunds(bookingId);
    }
  }

  /**
   * Writes down a transfer the venue has made. The list that comes back is what the row shows
   * afterwards — the server decides what is still owed, and it is the same answer that refuses
   * an amount larger than that.
   */
  protected recordRefund(booking: VenueBooking): void {
    this.refundForm.markAllAsTouched();
    if (this.refundForm.invalid || this.deciding()) {
      return;
    }

    const { amountBaht, refundedOn, method, note } = this.refundForm.getRawValue();

    this.sending(
      this.bookings.recordRefund(this.venueId(), booking.bookingId, {
        amountBaht: amountBaht!,
        refundedOn: plainDate(refundedOn),
        method,
        note: note.trim() || undefined,
      }),
      booking,
    );
  }

  /** Opens the reason box against one record, so what is typed can only reach that one. */
  protected askVoid(refundId: string): void {
    this.voiding.set(refundId);
    this.voidForm.reset({ reason: '' });
    this.decideError.set(null);
  }

  /** Taking a record back. The owner's alone, and the server says so if it is not them. */
  protected voidRefund(booking: VenueBooking, refundId: string): void {
    this.voidForm.markAllAsTouched();
    const reason = this.voidForm.getRawValue().reason.trim();

    if (reason.length === 0 || this.voidForm.invalid || this.deciding()) {
      return;
    }

    this.sending(
      this.bookings.voidRefund(this.venueId(), booking.bookingId, refundId, reason),
      booking,
    );
  }

  private openRefunds(bookingId: string): void {
    this.bookings.refunds(this.venueId(), bookingId).subscribe({
      next: (refunds) => {
        // The counter may have moved on while this was in the air. Answering into whichever row
        // is open now would show one booking's records under another's name — and prefill the
        // amount from the wrong debt (PRD US-18).
        if (this.asking()?.bookingId !== bookingId) {
          return;
        }

        this.refunds.set(refunds);
        this.readyToRecord(refunds.outstandingBaht);
      },
      error: (failure: unknown) => this.decideError.set(errorKey(failure)),
    });
  }

  /**
   * Ready for the next transfer: the amount starts at what is still owed, because sending all of
   * it is what usually happens and typing it again is only a chance to type it wrong.
   */
  private readyToRecord(outstanding: number): void {
    this.refundForm.reset({
      amountBaht: outstanding > 0 ? outstanding : null,
      refundedOn: venueToday(),
      method: 'Transfer',
      note: '',
    });
  }

  /**
   * A refund write answers with the refunds rather than with the booking, so the row's own
   * numbers are read again afterwards — what is owed has not moved, but what is left has.
   */
  private sending(refunds: Observable<Refunds>, booking: VenueBooking): void {
    this.deciding.set(true);
    this.decideError.set(null);

    refunds.subscribe({
      next: (sent) => {
        this.deciding.set(false);
        this.refunds.set(sent);
        this.voiding.set(null);
        this.readyToRecord(sent.outstandingBaht);

        this.bookings$.update((day) =>
          day.map((row) =>
            row.bookingId === booking.bookingId
              ? {
                  ...row,
                  sentBackBaht: sent.sentBackBaht,
                  outstandingBaht: sent.outstandingBaht,
                }
              : row,
          ),
        );
      },
      error: (failure: unknown) => {
        this.deciding.set(false);
        this.decideError.set(errorKey(failure));
      },
    });
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
      this.decide(this.bookings.cancel(this.venueId(), booking.bookingId, { paymentReceived }));
      return;
    }

    const { reason, note } = this.cancelForm.getRawValue();
    if (reason === null) {
      this.decideError.set('error.booking.reason_required');
      return;
    }

    this.decide(
      this.bookings.cancel(this.venueId(), booking.bookingId, {
        reason,
        note: note.trim() || undefined,
      }),
    );
  }

  protected noShow(booking: VenueBooking): void {
    this.decide(this.bookings.noShow(this.venueId(), booking.bookingId));
  }

  protected settle(booking: VenueBooking, paymentReceived: boolean): void {
    this.decide(this.bookings.settlePayment(this.venueId(), booking.bookingId, paymentReceived));
  }

  protected playedAfterAll(booking: VenueBooking): void {
    this.playedForm.markAllAsTouched();
    if (this.playedForm.invalid) {
      return;
    }

    this.decide(
      this.bookings.playedAfterAll(
        this.venueId(),
        booking.bookingId,
        this.playedForm.getRawValue().reason.trim(),
      ),
    );
  }

  /** Whether there is any money to say a line about: what is owed, owed back, or unanswered. */
  protected owesSomething(booking: VenueBooking): boolean {
    return (
      booking.can.takeMoney || booking.refundDueBaht > 0 || booking.paymentState === 'Unconfirmed'
    );
  }

  protected hours(booking: VenueBooking): string {
    return hoursOf(booking.slots);
  }

  protected courts(booking: VenueBooking): string {
    return courtsOf(booking.slots);
  }

  /**
   * A door was pressed. One booking comes back changed and replaces its own row rather than
   * reloading the day: the counter may be halfway through reading it. A refusal is the one case
   * the day is read again for — it usually means somebody else decided first.
   */
  private decide(decision: Observable<VenueBooking>): void {
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

  /** A block on the board was pressed: put that row where the counter is looking. */
  protected reveal(bookingId: string): void {
    document
      .querySelector(`[data-testid="booking-${bookingId}"]`)
      ?.scrollIntoView({ block: 'center', behavior: 'smooth' });
  }

  /** An empty hour was pressed: open the counter's own form at it (PRD US-13, US-25). */
  protected sellAt(_at: { courtId: string; hour: number }): void {
    this.selling.set(true);
  }

  /** Writes down what just changed hands (PRD US-26). */
  protected takePayment(booking: VenueBooking): void {
    this.takeForm.markAllAsTouched();
    if (this.takeForm.invalid) {
      return;
    }

    const { amountBaht, method, note } = this.takeForm.getRawValue();
    this.decide(
      this.bookings.takePayment(
        this.venueId(),
        booking.bookingId,
        amountBaht!,
        method,
        note.trim() || undefined,
      ),
    );
  }

  /** They rang to say they are coming (PRD US-24). */
  protected confirmArrival(booking: VenueBooking): void {
    this.decide(this.bookings.confirmArrival(this.venueId(), booking.bookingId));
  }

  /** They are at the desk. */
  protected checkIn(booking: VenueBooking): void {
    this.decide(this.bookings.checkIn(this.venueId(), booking.bookingId));
  }

  /**
   * A booking was just taken at the counter. The day is read again rather than patched, because
   * the new row belongs among the others in play order, and the server is what knows that order.
   */
  protected sold(): void {
    this.selling.set(false);
    this.load(this.venueId(), this.day(), { quiet: true });
  }

  private load(venueId: string, day: string, { quiet = false } = {}): void {
    this.loading.set(!quiet);
    this.pageError.set(null);

    // The floor the board is drawn on, asked for once per day rather than after every decision:
    // which courts a venue has and which hours it sells do not change because somebody paid.
    // It fails quietly — a day's bookings are readable without the board.
    if (!quiet) {
      this.venues.availability(venueId, day).subscribe({
        next: (grid) => this.grid.set(grid),
        error: () => this.grid.set(null),
      });

      // Who wanted this day and did not get it, asked for with the day rather than after every
      // decision: a queue does not change because somebody paid (PRD US-27). It fails quietly
      // for the same reason the grid does.
      this.waitlist.queue(venueId, day).subscribe({
        next: (queue) => this.waiting.set(queue),
        error: () => this.waiting.set([]),
      });
    }

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
