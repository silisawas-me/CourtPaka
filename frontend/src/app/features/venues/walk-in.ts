import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  input,
  output,
  signal,
  viewChild,
  afterNextRender,
} from '@angular/core';
import { errorKey } from '../../core/http/api-error';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { venueNow } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { Availability, PublicVenueService } from '../../core/venues/public-venue.service';
import { CounterPayment, VenueBookingsService } from '../../core/venues/venue-bookings.service';
import { WalkInEvents } from '../../core/venues/walk-in.events';
import { freeCourts, priceOf, startOptions, WALK_IN_HOURS } from './walk-in-rules';

/** The two ways money arrives at the counter (O5: no TrueMoney). */
const PAYMENTS: readonly CounterPayment[] = ['Transfer', 'Cash'];

/**
 * Selling a court to somebody standing at the counter, from the top bar (owner app PR-3): a
 * start, a length, a court that is free for all of it, a name, and how they paid — the design's
 * walk-in modal. It is the counter sale that already exists (PRD US-13), asked a different way:
 * the same request, the same server rules, so what it can sell is what the counter can sell.
 *
 * Today only: a walk-in is somebody here now. Selling another day is the day console's job.
 */
@Component({
  selector: 'app-walk-in',
  imports: [BahtPipe],
  templateUrl: './walk-in.html',
  styleUrl: './walk-in.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown.escape)': 'closed.emit()' },
})
export class WalkIn {
  private readonly venues = inject(PublicVenueService);
  private readonly bookings = inject(VenueBookingsService);
  private readonly events = inject(WalkInEvents);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  /** A court and hour to open on — somebody tapped that empty cell on the timeline. */
  readonly at = input<{ courtId: string; hour: number } | null>(null);
  readonly closed = output<void>();

  protected readonly payments = PAYMENTS;
  protected readonly lengths = WALK_IN_HOURS;

  private readonly today = venueNow();
  protected readonly nowHour = this.today.hour;
  protected readonly day = signal<Availability | null>(null);
  protected readonly loadError = signal<string | null>(null);

  protected readonly start = signal<number | null>(null);
  protected readonly hours = signal<number>(1);
  protected readonly court = signal<string | null>(null);
  protected readonly name = signal('');
  protected readonly phone = signal('');
  protected readonly paidBy = signal<CounterPayment>('Transfer');
  protected readonly sending = signal(false);
  protected readonly error = signal<string | null>(null);

  private readonly nameField = viewChild<ElementRef<HTMLInputElement>>('nameField');

  protected readonly starts = computed(() => {
    const day = this.day();
    if (!day) {
      return [];
    }
    // The tapped hour is on offer even when it is further off than the usual first few.
    const options = startOptions(day, this.today.hour);
    const asked = this.at()?.hour;
    return asked !== undefined && !options.includes(asked)
      ? [...options, asked].sort((left, right) => left - right)
      : options;
  });

  protected readonly courts = computed(() => {
    const day = this.day();
    const start = this.start();
    if (!day || start === null) {
      return [];
    }
    const free = new Set(freeCourts(day, start, this.hours()));
    return day.courts.map((court) => ({
      id: court.courtId,
      name: court.name,
      free: free.has(court.courtId),
    }));
  });

  protected readonly total = computed(() => {
    const day = this.day();
    const start = this.start();
    return day && start !== null ? priceOf(day, start, this.hours(), this.court()) : null;
  });

  protected readonly ready = computed(
    () => this.court() !== null && this.name().trim().length > 0 && !this.sending(),
  );

  constructor() {
    afterNextRender(() => {
      this.venues.availability(this.venueId(), this.today.date, true).subscribe({
        next: (day) => {
          this.day.set(day);
          const at = this.at();
          this.start.set(
            at && this.starts().includes(at.hour) ? at.hour : (this.starts()[0] ?? null),
          );
          // The tapped court, if it is free for the hour: what is left to ask is who and how.
          if (at && this.courts().some((court) => court.id === at.courtId && court.free)) {
            this.court.set(at.courtId);
          }
        },
        error: (failure: unknown) => this.loadError.set(errorKey(failure)),
      });
    });
  }

  protected pickStart(hour: number): void {
    this.start.set(hour);
    this.keepCourtIfFree();
  }

  protected pickHours(hours: number): void {
    this.hours.set(hours);
    this.keepCourtIfFree();
  }

  protected pickCourt(courtId: string): void {
    this.court.set(courtId);
    // The name is what is asked next, and a counter has a customer waiting.
    this.nameField()?.nativeElement.focus();
  }

  protected typed(field: 'name' | 'phone', event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    (field === 'name' ? this.name : this.phone).set(value);
  }

  protected confirm(): void {
    const start = this.start();
    const court = this.court();
    if (!this.ready() || start === null || court === null) {
      return;
    }

    this.sending.set(true);
    this.error.set(null);
    this.bookings
      .takeAtCounter(this.venueId(), {
        slots: Array.from({ length: this.hours() }, (_, index) => ({
          courtId: court,
          date: this.today.date,
          hour: start + index,
        })),
        customerName: this.name().trim(),
        customerPhone: this.phone().trim() || null,
        paidBy: this.paidBy(),
      })
      .subscribe({
        next: () => {
          this.events.sold.next({ venueId: this.venueId() });
          this.closed.emit();
        },
        error: (failure: unknown) => {
          this.sending.set(false);
          this.error.set(errorKey(failure));
        },
      });
  }

  /** A court chosen for one start and length stays chosen only while it is free for the new one. */
  private keepCourtIfFree(): void {
    const court = this.court();
    if (court && !this.courts().some((one) => one.id === court && one.free)) {
      this.court.set(null);
    }
  }
}
