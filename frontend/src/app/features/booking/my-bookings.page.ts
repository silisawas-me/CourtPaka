import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { Booking, BookingHistory, BookingService } from '../../core/bookings/booking.service';
import { courtsOf, hoursOf } from '../../core/bookings/hours';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { TranslationService } from '../../core/i18n/translation.service';

/**
 * Everything a booker has taken, and the way back out of one (PRD US-05).
 *
 * The question this page answers is "when do I play next, and where", so what is ahead of them
 * comes first and in the order it will happen. What is behind is a record, so it stays newest
 * first and out of the way.
 *
 * Letting a booking go opens inside its own card rather than over the page: what is being given
 * up, and what comes back for it, have to be readable while the question is being asked.
 */
@Component({
  selector: 'app-my-bookings-page',
  imports: [RouterLink, MatButtonModule, MatCardModule, MatProgressBarModule, AppDatePipe],
  templateUrl: './my-bookings.page.html',
  styleUrl: './my-bookings.page.scss',
})
export class MyBookingsPage {
  private readonly bookings = inject(BookingService);

  protected readonly i18n = inject(TranslationService);

  private readonly history = signal<BookingHistory>({ upcoming: [], past: [] });

  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  /**
   * The two lists in the order they are read, each booking already worded. One loop draws both,
   * so a card gains a field in one place rather than in two that could drift apart, and the
   * wording is worked out when the list arrives rather than on every redraw.
   */
  protected readonly groups = computed(() => {
    const history = this.history();
    return [
      { key: 'upcoming', bookings: history.upcoming.map(card) },
      { key: 'past', bookings: history.past.map(card) },
    ];
  });

  protected readonly nothingAtAll = computed(
    () =>
      !this.loading() && this.history().upcoming.length === 0 && this.history().past.length === 0,
  );

  /** Which booking is being asked about. One at a time: this gives hours up for good. */
  protected readonly letting = signal<string | null>(null);
  protected readonly cancelling = signal(false);
  protected readonly cancelError = signal<string | null>(null);

  constructor() {
    this.load();
  }

  protected ask(bookingId: string): void {
    this.letting.set(bookingId);
    this.cancelError.set(null);
  }

  protected keep(): void {
    this.letting.set(null);
    this.cancelError.set(null);
  }

  protected letGo(booking: Booking): void {
    if (this.cancelling()) {
      return;
    }

    this.cancelling.set(true);
    this.cancelError.set(null);

    this.bookings.cancel(booking.id).subscribe({
      next: () => {
        this.cancelling.set(false);
        this.letting.set(null);
        // Read the list again rather than moving the card here: a cancelled booking belongs
        // behind them now, and where it belongs is the server's to say (PRD US-05).
        this.load({ quiet: true });
      },
      error: (failure: unknown) => {
        this.cancelling.set(false);
        this.cancelError.set(errorKey(failure));
        // Usually the venue decided first, so what is on screen is already out of date.
        this.load({ quiet: true });
      },
    });
  }

  private load({ quiet = false } = {}): void {
    this.loading.set(!quiet);
    this.pageError.set(null);

    this.bookings.mine().subscribe({
      next: (history) => {
        this.history.set(history);
        this.loading.set(false);
      },
      error: (failure: unknown) => {
        if (!quiet) {
          this.pageError.set(errorKey(failure));
        }
        this.loading.set(false);
      },
    });
  }
}

/** A booking with the two lines the card reads it by. */
export interface BookingCard extends Booking {
  when: string;
  courts: string;
}

function card(booking: Booking): BookingCard {
  return { ...booking, when: hoursOf(booking.slots), courts: courtsOf(booking.slots) };
}
