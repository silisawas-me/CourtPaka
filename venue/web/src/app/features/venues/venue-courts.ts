import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { forkJoin, Observable } from 'rxjs';
import { ApiError, errorKey } from '../../core/http/api-error';
import { venueNow } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import {
  BlockingBooking,
  Court,
  CourtClosure,
  CourtService,
} from '../../core/venues/court.service';

/** How a court stands today, in the words of the artboard. */
type CourtState = 'inUse' | 'closedNow' | 'outOfUse';

/**
 * The venue's courts and their closures (docs/plan/thai-fit.md, "คอร์ต & ปิดซ่อม"): add one,
 * rename one, take one out of use for good, or shut one for a while. A closure the server
 * refuses comes back with the bookings in its way, and the page says where to deal with them —
 * it does not cancel anybody, because a cancellation decides what is owed (PRD US-11).
 */
@Component({
  selector: 'app-venue-courts',
  imports: [RouterLink],
  templateUrl: './venue-courts.html',
  styleUrl: './venue-settings-tab.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VenueCourts {
  private readonly service = inject(CourtService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  readonly canManage = input(false);

  protected readonly courts = signal<Court[]>([]);
  protected readonly closures = signal<CourtClosure[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected readonly newName = signal('');
  protected readonly renaming = signal<string | null>(null);
  protected readonly renameTo = signal('');

  /** The closure form: which court, from when, until when, why. */
  protected readonly closing = signal<string | null>(null);
  protected readonly startsOn = signal(venueNow().date);
  protected readonly startHour = signal(venueNow().hour + 1);
  protected readonly endsOn = signal(venueNow().date);
  protected readonly endHour = signal(Math.min(24, venueNow().hour + 3));
  protected readonly reason = signal('');
  protected readonly blocking = signal<BlockingBooking[]>([]);
  protected readonly hours = Array.from({ length: 25 }, (_, hour) => hour);

  /** Closures still running or still to come, which are the ones anybody can do anything about. */
  protected readonly standing = computed(() => {
    const now = venueNow();
    return this.closures().filter(
      (one) =>
        !one.liftedAt &&
        (one.endsOn > now.date || (one.endsOn === now.date && one.endHour > now.hour)),
    );
  });

  protected readonly rows = computed(() => {
    const now = venueNow();
    return this.courts().map((court) => {
      const shut = this.standing().find(
        (one) =>
          one.courtId === court.id &&
          (one.startsOn < now.date || (one.startsOn === now.date && one.startHour <= now.hour)),
      );
      const state: CourtState = !court.isActive ? 'outOfUse' : shut ? 'closedNow' : 'inUse';
      return { court, state, shut };
    });
  });

  protected readonly closingCourt = computed(
    () => this.courts().find((court) => court.id === this.closing()) ?? null,
  );

  constructor() {
    effect(() => this.read(this.venueId()));
  }

  protected courtName(courtId: string): string {
    return this.courts().find((court) => court.id === courtId)?.name ?? '';
  }

  protected add(): void {
    const name = this.newName().trim();
    if (!name || this.busy()) {
      return;
    }
    this.run(this.service.addCourt(this.venueId(), name), () => this.newName.set(''));
  }

  protected startRename(court: Court): void {
    this.renaming.set(court.id);
    this.renameTo.set(court.name);
  }

  protected rename(court: Court): void {
    const name = this.renameTo().trim();
    if (!name || this.busy()) {
      return;
    }
    this.run(this.service.updateCourt(this.venueId(), court.id, name, court.position), () =>
      this.renaming.set(null),
    );
  }

  protected setInUse(court: Court, active: boolean): void {
    this.run(this.service.changeCourtStatus(this.venueId(), court.id, active));
  }

  protected openClosing(court: Court): void {
    this.closing.set(court.id);
    this.blocking.set([]);
    this.error.set(null);
  }

  protected close(): void {
    const courtId = this.closing();
    if (!courtId || !this.reason().trim() || this.busy()) {
      return;
    }
    this.blocking.set([]);
    this.run(
      this.service.closeCourt(this.venueId(), courtId, {
        startsOn: this.startsOn(),
        startHour: Number(this.startHour()),
        endsOn: this.endsOn(),
        endHour: Number(this.endHour()),
        reason: this.reason().trim(),
      }),
      () => {
        this.closing.set(null);
        this.reason.set('');
      },
    );
  }

  protected lift(closure: CourtClosure): void {
    this.run(this.service.liftClosure(this.venueId(), closure.id));
  }

  private run(door: Observable<unknown>, after?: () => void): void {
    this.busy.set(true);
    this.error.set(null);
    door.subscribe({
      next: () => {
        this.busy.set(false);
        after?.();
        this.read(this.venueId());
      },
      error: (failure: unknown) => {
        this.busy.set(false);
        this.error.set(errorKey(failure));
        // A closure refused because people booked those hours: say who stands in the way.
        const bookings = failure instanceof ApiError ? failure.details?.['bookings'] : null;
        if (Array.isArray(bookings)) {
          this.blocking.set(bookings as BlockingBooking[]);
        }
      },
    });
  }

  private read(venueId: string): void {
    forkJoin({
      courts: this.service.courts(venueId),
      closures: this.service.closures(venueId),
    }).subscribe({
      next: ({ courts, closures }) => {
        this.courts.set(courts);
        this.closures.set(closures);
      },
      error: (failure: unknown) => this.error.set(errorKey(failure)),
    });
  }
}
