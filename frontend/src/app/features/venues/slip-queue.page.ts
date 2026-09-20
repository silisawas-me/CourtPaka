import { Component, computed, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { errorKey } from '../../core/http/api-error';
import { AppDateTimePipe } from '../../core/i18n/app-date.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import { SlipQueueItem, SlipQueueService } from '../../core/venues/slip-queue.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

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
  private readonly destroyed = inject(DestroyRef);

  protected readonly i18n = inject(TranslationService);

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
  protected readonly slipUnavailable = signal(false);

  protected readonly deciding = signal(false);
  protected readonly decideError = signal<string | null>(null);

  /** Turning a booking away needs a reason and an answer about the money (PRD 6.1). */
  protected readonly rejecting = signal(false);

  protected readonly rejection = inject(FormBuilder).nonNullable.group({
    reason: ['', [Validators.required, Validators.maxLength(500)]],
    paymentReceived: [false],
  });

  constructor() {
    effect(() => this.load(this.venueId()));

    // A blob URL is a handle the browser holds until it is told to let go.
    this.destroyed.onDestroy(() => this.releaseSlip());
  }

  protected open(bookingId: string): void {
    this.openedId.set(bookingId);
    this.rejecting.set(false);
    this.decideError.set(null);
    this.rejection.reset({ reason: '', paymentReceived: false });
    this.showSlip(bookingId);
  }

  protected startRejecting(): void {
    this.rejecting.set(true);
    this.decideError.set(null);
  }

  protected cancelRejecting(): void {
    this.rejecting.set(false);
    this.rejection.reset({ reason: '', paymentReceived: false });
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
    this.decide(this.slips.reject(this.venueId(), bookingId, reason, paymentReceived), bookingId);
  }

  private decide(decision: ReturnType<SlipQueueService['confirm']>, bookingId: string): void {
    this.deciding.set(true);
    this.decideError.set(null);

    decision.subscribe({
      next: () => {
        // Decided means gone from the queue. The next one opens by itself, so the venue keeps
        // working rather than choosing again.
        this.deciding.set(false);
        this.rejecting.set(false);
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

  private openNext(): void {
    const next = this.queue()[0]?.bookingId ?? null;
    this.openedId.set(next);
    this.releaseSlip();
    if (next !== null) {
      this.showSlip(next);
    }
  }

  private showSlip(bookingId: string): void {
    this.releaseSlip();
    this.slipUnavailable.set(false);

    this.slips.slip(this.venueId(), bookingId).subscribe({
      next: (blob) => this.slipUrl.set(URL.createObjectURL(blob)),
      // The picture is the point of the page, so its absence is said out loud rather than
      // left as an empty frame.
      error: () => this.slipUnavailable.set(true),
    });
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
