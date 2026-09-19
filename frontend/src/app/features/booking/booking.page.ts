import { Component, computed, DestroyRef, effect, inject, input, signal } from '@angular/core';
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
export class BookingPage {
  private readonly bookings = inject(BookingService);
  private readonly destroyed = inject(DestroyRef);

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

  /**
   * How long is left on the hold, as the page shows it, or null when there is nothing to count —
   * which is every status but Held, and is what the template branches on.
   */
  protected readonly countdown = computed(() => {
    const held = this.booking();
    if (held?.status !== 'Held') {
      return null;
    }
    const left = Math.max(
      0,
      Math.floor((new Date(held.holdExpiresAt).getTime() - this.now()) / 1000),
    );
    return left === 0
      ? this.i18n.t('booking.pay.timeUp')
      : `${this.i18n.t('booking.pay.timeLeft')} ${Math.floor(left / 60)}:${`${left % 60}`.padStart(2, '0')}`;
  });

  /** A slip is worth sending while the booking is held or already waiting to be checked. */
  protected readonly canSend = computed(() => {
    const status = this.booking()?.status;
    return status === 'Held' || status === 'PendingVerification';
  });

  constructor() {
    effect(() => this.load(this.bookingId()));

    // The clock only runs while there is something to count down to.
    let ticking: ReturnType<typeof setInterval> | undefined;
    effect(() => {
      const counting = this.booking()?.status === 'Held';
      if (counting && ticking === undefined) {
        ticking = setInterval(() => this.now.set(Date.now()), TICK_MS);
      } else if (!counting && ticking !== undefined) {
        clearInterval(ticking);
        ticking = undefined;
      }
    });

    this.destroyed.onDestroy(() => clearInterval(ticking));
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
        // A refusal usually means the booking moved on, so show what it says now — without
        // blanking the page that is showing the reason.
        this.load(this.bookingId(), { quiet: true });
      },
    });
  }

  /** Quiet means: keep what is on screen, because something beside it is explaining why. */
  private load(bookingId: string, { quiet = false } = {}): void {
    this.loading.set(!quiet);
    this.pageError.set(null);

    this.bookings.get(bookingId).subscribe({
      next: (booking) => {
        this.booking.set(booking);
        this.loading.set(false);
      },
      error: (failure: unknown) => {
        // A quiet read is a second opinion, not the page's own content: if it fails, what is on
        // screen — including the reason the upload was refused — is still the better answer.
        if (!quiet) {
          this.booking.set(null);
          this.pageError.set(errorKey(failure));
        }
        this.loading.set(false);
      },
    });
  }
}
