import { Component, computed, effect, inject, input, OnDestroy, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import {
  Booking,
  BookingService,
  SLIP_ACCEPT,
  SLIP_MAX_BYTES,
} from '../../core/bookings/booking.service';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { TranslationService } from '../../core/i18n/translation.service';

/** The countdown ticks once a second; anything faster only redraws the same number. */
const TICK_MS = 1000;

/**
 * One booking: what it holds, what it costs, how long is left to pay for it, and the picture of
 * the transfer (PRD US-04). The venue checking that picture is US-12.
 *
 * The clock on this page is only a clock. The server decides whether a slip arrived in time, and
 * says so when it did not — a page whose countdown has run out is still worth sending from, in
 * case the two disagree by a second.
 */
@Component({
  selector: 'app-booking-page',
  imports: [RouterLink, MatButtonModule, MatCardModule, MatProgressBarModule, AppDatePipe],
  templateUrl: './booking.page.html',
  styleUrl: './booking.page.scss',
})
export class BookingPage implements OnDestroy {
  private readonly bookings = inject(BookingService);

  protected readonly i18n = inject(TranslationService);
  protected readonly accept = SLIP_ACCEPT;

  readonly bookingId = input.required<string>();

  protected readonly booking = signal<Booking | null>(null);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);
  protected readonly uploading = signal(false);
  protected readonly uploadError = signal<string | null>(null);

  /** Ticks so the countdown redraws; the value itself comes from the booking. */
  private readonly now = signal(Date.now());

  /** Seconds left on the hold, floored at zero. Null once the booking is no longer held. */
  protected readonly secondsLeft = computed(() => {
    const held = this.booking();
    if (held?.status !== 'Held') {
      return null;
    }
    const left = new Date(held.holdExpiresAt).getTime() - this.now();
    return Math.max(0, Math.floor(left / 1000));
  });

  protected readonly countdown = computed(() => {
    const left = this.secondsLeft();
    if (left === null) {
      return '';
    }
    const minutes = Math.floor(left / 60);
    return `${minutes}:${`${left % 60}`.padStart(2, '0')}`;
  });

  /** A slip is worth sending while the booking is held or already waiting to be checked. */
  protected readonly canSend = computed(() => {
    const status = this.booking()?.status;
    return status === 'Held' || status === 'PendingVerification';
  });

  private readonly ticking = setInterval(() => this.now.set(Date.now()), TICK_MS);

  constructor() {
    effect(() => this.load(this.bookingId()));
  }

  ngOnDestroy(): void {
    clearInterval(this.ticking);
  }

  protected send(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';

    if (!file) {
      return;
    }

    if (file.size > SLIP_MAX_BYTES) {
      this.uploadError.set('error.slip.too_large');
      return;
    }

    this.uploading.set(true);
    this.uploadError.set(null);

    this.bookings.uploadSlip(this.bookingId(), file).subscribe({
      next: (booking) => {
        this.booking.set(booking);
        this.uploading.set(false);
      },
      error: (failure: unknown) => {
        this.uploadError.set(errorKey(failure));
        this.uploading.set(false);
        // A refusal usually means the booking moved on, so show what it says now.
        this.load(this.bookingId());
      },
    });
  }

  private load(bookingId: string): void {
    this.loading.set(true);
    this.pageError.set(null);

    this.bookings.get(bookingId).subscribe({
      next: (booking) => {
        this.booking.set(booking);
        this.loading.set(false);
      },
      error: (failure: unknown) => {
        this.booking.set(null);
        this.pageError.set(errorKey(failure));
        this.loading.set(false);
      },
    });
  }
}
