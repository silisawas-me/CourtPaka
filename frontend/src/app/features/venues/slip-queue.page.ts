import { Component, computed, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import {
  AbstractControl,
  FormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { catchError, EMPTY, Observable, switchMap, tap } from 'rxjs';
import { Booking } from '../../core/bookings/booking.service';
import { errorKey } from '../../core/http/api-error';
import { AppDateTimePipe } from '../../core/i18n/app-date.pipe';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import { SlipQueueItem, SlipQueueService } from '../../core/venues/slip-queue.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

/** As much as the venue may write, and as much as the column holds (BookingStatusChange). */
const REASON_MAX_LENGTH = 500;

/** A reason of nothing but spaces is no reason, which is what the server says too. */
function written(control: AbstractControl<string>): ValidationErrors | null {
  return control.value.trim().length > 0 ? null : { required: true };
}

/**
 * The queue of slips a venue has been sent, and the two answers (PRD US-12).
 *
 * Deciding is comparing a picture against an amount, so those two sit together and everything else
 * is around them. One booking is open at a time: a row of thumbnails would invite guessing, and
 * this decision moves money.
 */
@Component({
  selector: 'app-slip-queue-page',
  imports: [
    BahtPipe,
    ReactiveFormsModule,
    RouterLink,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    AppDateTimePipe,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './slip-queue.page.html',
  styleUrl: './slip-queue.page.scss',
})
export class SlipQueuePage {
  private readonly slips = inject(SlipQueueService);
  private readonly forms = inject(FormBuilder);
  private readonly destroyed = inject(DestroyRef);

  protected readonly i18n = inject(TranslationService);
  protected readonly reasonMaxLength = REASON_MAX_LENGTH;

  readonly venueId = input.required<string>();

  protected readonly queue = signal<SlipQueueItem[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  /** Which booking is open. The first one, until the venue picks another. */
  protected readonly openedId = signal<string | null>(null);

  protected readonly opened = computed(
    () => this.queue().find((item) => item.bookingId === this.openedId()) ?? null,
  );

  /** The picture, fetched rather than linked: the endpoint needs the session cookie. */
  protected readonly slipUrl = signal<string | null>(null);

  /** A slip may be a photograph or a PDF (PRD US-04), and the two are not shown the same way. */
  protected readonly slipIsPdf = signal(false);
  protected readonly slipUnavailable = signal(false);

  protected readonly deciding = signal(false);
  protected readonly decideError = signal<string | null>(null);

  /** Turning a booking away needs a reason and an answer about the money (PRD 6.1). */
  protected readonly rejecting = signal(false);

  protected readonly rejection = this.forms.group({
    reason: this.forms.nonNullable.control('', [written, Validators.maxLength(REASON_MAX_LENGTH)]),
    // No default: the answer decides whether money goes back, so the venue says it rather than
    // agreeing to whatever was already ticked (PRD US-12).
    paymentReceived: this.forms.control<boolean | null>(null, Validators.required),
  });

  constructor() {
    effect(() => this.load(this.venueId()));

    // switchMap drops the picture of a booking the venue has already moved off. A big slip
    // answering after a small one would otherwise be left on screen beside somebody else's
    // amount — and what is decided here moves money.
    toObservable(computed(() => ({ venueId: this.venueId(), bookingId: this.openedId() })))
      .pipe(
        tap(() => this.releaseSlip()),
        switchMap(({ venueId, bookingId }) =>
          bookingId === null
            ? EMPTY
            : this.slips.slip(venueId, bookingId).pipe(
                // The picture is the point of the page, so its absence is said out loud rather
                // than left as an empty frame.
                catchError(() => {
                  this.slipUnavailable.set(true);
                  return EMPTY;
                }),
              ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe((slip) => {
        this.slipIsPdf.set(slip.type === 'application/pdf');
        this.slipUrl.set(URL.createObjectURL(slip));
      });

    // A blob URL is a handle the browser holds until it is told to let go.
    this.destroyed.onDestroy(() => this.releaseSlip());
  }

  /** The venue picked a row. Whatever the last answer said is done with. */
  protected open(bookingId: string): void {
    this.decideError.set(null);
    this.show(bookingId);
  }

  protected startRejecting(): void {
    this.rejecting.set(true);
    this.decideError.set(null);
  }

  protected cancelRejecting(): void {
    this.rejecting.set(false);
    this.rejection.reset({ reason: '', paymentReceived: null });
  }

  protected confirm(): void {
    const bookingId = this.openedId();
    if (bookingId === null || this.deciding()) {
      return;
    }

    this.decide(this.slips.confirm(this.venueId(), bookingId), bookingId);
  }

  protected reject(): void {
    const bookingId = this.openedId();
    this.rejection.markAllAsTouched();
    if (bookingId === null || this.deciding() || this.rejection.invalid) {
      return;
    }

    const { reason, paymentReceived } = this.rejection.getRawValue();
    this.decide(
      this.slips.reject(this.venueId(), bookingId, reason.trim(), paymentReceived!),
      bookingId,
    );
  }

  private decide(decision: Observable<Booking>, bookingId: string): void {
    this.deciding.set(true);
    this.decideError.set(null);

    decision.subscribe({
      next: () => {
        // Decided means gone from the queue. The next one opens by itself, so the venue keeps
        // working rather than choosing again.
        this.deciding.set(false);
        this.queue.update((items) => items.filter((item) => item.bookingId !== bookingId));
        this.openNext();
      },
      error: (failure: unknown) => {
        this.decideError.set(errorKey(failure));
        this.deciding.set(false);
        // A refusal usually means someone else got there first, so read the queue again.
        this.load(this.venueId(), { quiet: true });
      },
    });
  }

  /** Opens whatever is at the head of the queue, or nothing if the queue is empty. */
  private openNext(): void {
    this.show(this.queue()[0]?.bookingId ?? null);
  }

  /**
   * Moves to a booking. The rejection form is emptied on the way: a reason typed about one
   * booking must not be sitting in the box above the next one.
   */
  private show(bookingId: string | null): void {
    this.openedId.set(bookingId);
    this.cancelRejecting();
    this.slipUnavailable.set(false);
  }

  private releaseSlip(): void {
    const url = this.slipUrl();
    if (url !== null) {
      URL.revokeObjectURL(url);
      this.slipUrl.set(null);
    }
  }

  private load(venueId: string, { quiet = false } = {}): void {
    this.loading.set(!quiet);
    this.pageError.set(null);

    this.slips.queue(venueId).subscribe({
      next: (queue) => {
        this.queue.set(queue);
        this.loading.set(false);
        if (this.opened() === null) {
          this.openNext();
        }
      },
      error: (failure: unknown) => {
        if (!quiet) {
          this.queue.set([]);
          this.pageError.set(errorKey(failure));
        }
        this.loading.set(false);
      },
    });
  }
}
