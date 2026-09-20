import { Component, computed, DestroyRef, effect, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import {
  Booking,
  BookingService,
  PaymentDetails,
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

  /**
   * Where to send the money, once it has been asked for. Its own request because a QR is only of
   * use while one booking is waiting to be paid for (PRD US-04).
   */
  protected readonly payment = signal<PaymentDetails | null>(null);

  /** The QR as a data URI, drawn in the browser from the payload the server built. */
  protected readonly qr = signal<string | null>(null);

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
  /**
   * Asks where to send the money, once, while there is money to send. The QR is drawn from the
   * payload rather than fetched as a picture, so the code the bank app reads is the one the
   * server built and nothing in between gets to change the amount (PRD US-04).
   */
  private askHowToPay(bookingId: string, booking: Booking): void {
    const owing = booking.status === 'Held' || booking.status === 'PendingVerification';
    if (!owing || this.payment() !== null) {
      return;
    }

    this.bookings.payment(bookingId).subscribe({
      next: (how) => {
        this.payment.set(how);
        if (how.promptPayPayload !== null) {
          void this.draw(how.promptPayPayload);
        }
      },
      // A code that cannot be fetched is not worth an error across the page: the account details
      // are on the venue's own page, and the slip upload below still works.
      error: () => this.payment.set(null),
    });
  }

  /**
   * The encoder is loaded only here. It is a few tens of kilobytes that every other page in the
   * app would otherwise carry to show a picture only this one draws.
   *
   * Drawn as SVG rather than through a canvas: it stays sharp at whatever size the phone gives
   * it, and it does not need a canvas to exist — which is also what makes it something a test
   * can look at rather than take on trust.
   */
  private async draw(payload: string): Promise<void> {
    try {
      // Taken off the module object rather than destructured. `toString` is a name every object
      // already has, so `const { toString } = await import(...)` silently picks up
      // Object.prototype's when the CommonJS interop puts the real one under `default` — which
      // fails at the call, in the browser only, with a minified name in the message.
      const loaded = await import('qrcode');
      const encoder = loaded.default ?? loaded;

      const svg = await encoder.toString(payload, {
        type: 'svg',
        errorCorrectionLevel: 'M',
        margin: 1,
      });

      // Percent-encoded rather than base64: the SVG carries Unicode, and the round trip through
      // btoa needs a dance with escape() that is deprecated and easy to get subtly wrong.
      this.qr.set(`data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`);
    } catch {
      // No code drawn, and the account name below still says where the money goes. A page that
      // threw here would take the slip upload down with it.
      this.qr.set(null);
    }
  }

  private load(bookingId: string, { quiet = false } = {}): void {
    this.loading.set(!quiet);
    this.pageError.set(null);

    this.bookings.get(bookingId).subscribe({
      next: (booking) => {
        this.booking.set(booking);
        this.loading.set(false);
        this.askHowToPay(bookingId, booking);
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
