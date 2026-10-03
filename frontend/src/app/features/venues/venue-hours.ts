import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { concatMap, from, Observable, of, switchMap, toArray } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import { errorKey } from '../../core/http/api-error';
import { plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { CourtService, OpeningHoursDay, Weekday } from '../../core/venues/court.service';
import { PricingService } from '../../core/venues/pricing.service';
import { Venue, VenueService } from '../../core/venues/venue.service';
import { dayIsValid, latestWeek, pricesCovering } from './hours-rules';

/** The grace presets the design offers beside − and +, in minutes (0–60 is what the server takes). */
const GRACE_PRESETS = [0, 10, 15, 30, 60] as const;
const GRACE_MAX = 60;
const GRACE_STEP = 5;

/**
 * Opening hours and the wait for latecomers, in the pricing section (artboard c of
 * docs/plan/owner-complete.md): the week by day, from a date, and how many minutes after an hour
 * starts nobody having come counts. Saving a week that opens an hour nobody has priced prices it
 * from its nearest hour that day first (`pricesCovering`), because the server refuses an open
 * hour without a price and the price grid paints only hours that are open.
 */
@Component({
  selector: 'app-venue-hours',
  templateUrl: './venue-hours.html',
  styleUrl: './venue-hours.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VenueHours {
  private readonly courts = inject(CourtService);
  private readonly pricing = inject(PricingService);
  private readonly venues = inject(VenueService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  readonly venue = input<Venue | null>(null);
  readonly canManage = input(false);
  /** The owner's other venues, which "ใช้กับทุกสาขา" gives this week too. */
  readonly others = input<readonly Venue[]>([]);

  /** A week was saved, so the price grid's open hours changed. */
  readonly saved = output<void>();

  protected readonly openHours = Array.from({ length: 24 }, (_, hour) => hour);
  protected readonly closeHours = Array.from({ length: 24 }, (_, hour) => hour + 1);
  protected readonly presets = GRACE_PRESETS;
  protected readonly today = plainDate(venueToday());

  protected readonly week = signal<OpeningHoursDay[] | null>(null);
  protected readonly from = signal(plainDate(venueToday()));
  protected readonly loadError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly result = signal<{ text: string; error: boolean }[]>([]);

  protected readonly grace = signal(15);
  protected readonly graceSaved = signal(false);
  protected readonly graceError = signal<string | null>(null);

  protected readonly weekValid = computed(() => (this.week() ?? []).every(dayIsValid));

  constructor() {
    effect(() => this.load(this.venueId()));
    effect(() => {
      const minutes = this.venue()?.graceMinutes;
      if (minutes !== undefined) {
        this.grace.set(minutes);
      }
    });
  }

  protected dayName(day: Weekday): string {
    return this.i18n.t(`hours.day.${day}`);
  }

  protected hourLabel(hour: number): string {
    return `${String(hour).padStart(2, '0')}:00`;
  }

  /** Closing at 24 is midnight, and the artboard says so beside the number. */
  protected closeLabel(hour: number): string {
    return hour === 24
      ? `${this.hourLabel(hour)} ${this.i18n.t('hours.midnight')}`
      : this.hourLabel(hour);
  }

  protected setOpens(day: Weekday, value: string): void {
    this.edit(day, { opensHour: Number(value) });
  }

  protected setCloses(day: Weekday, value: string): void {
    this.edit(day, { closesHour: Number(value) });
  }

  /** Open on a shut day as the day before it opens (else 08–23), or shut an open one. */
  protected toggle(day: OpeningHoursDay): void {
    if (day.opensHour === null) {
      const open = (this.week() ?? []).find((one) => one.opensHour !== null);
      this.edit(day.day, {
        opensHour: open?.opensHour ?? 8,
        closesHour: open?.closesHour ?? 23,
      });
    } else {
      this.edit(day.day, { opensHour: null, closesHour: null });
    }
  }

  protected save(): void {
    const week = this.week();
    if (!week || !this.weekValid() || this.busy()) {
      return;
    }
    this.busy.set(true);
    this.result.set([]);
    this.publish(this.venueId(), week).subscribe((line) => {
      this.busy.set(false);
      this.result.set([line]);
      if (!line.error) {
        this.saved.emit();
      }
    });
  }

  /** The same week at every other venue this person owns, each saved (and refused) on its own. */
  protected saveEverywhere(): void {
    const week = this.week();
    if (!week || !this.weekValid() || this.busy()) {
      return;
    }
    this.busy.set(true);
    this.result.set([]);
    from([{ id: this.venueId(), name: this.venue()?.name ?? '' }, ...this.others()])
      .pipe(
        concatMap((venue) =>
          this.publish(venue.id, week).pipe(
            map((line) => ({ ...line, text: `${venue.name}: ${line.text}` })),
          ),
        ),
        toArray(),
      )
      .subscribe((lines) => {
        this.busy.set(false);
        this.result.set(lines);
        this.saved.emit();
      });
  }

  protected nudgeGrace(by: number): void {
    this.grace.update((minutes) => Math.max(0, Math.min(GRACE_MAX, minutes + by * GRACE_STEP)));
    this.graceSaved.set(false);
  }

  protected pickGrace(minutes: number): void {
    this.grace.set(minutes);
    this.graceSaved.set(false);
  }

  protected saveGrace(): void {
    this.graceError.set(null);
    this.venues.setGrace(this.venueId(), this.grace()).subscribe({
      next: () => this.graceSaved.set(true),
      error: (failure: unknown) => this.graceError.set(errorKey(failure)),
    });
  }

  /**
   * One venue's week: its prices first when the week opens an hour it has not priced, then the
   * week itself. Answers one line to show, whichever way it went.
   */
  private publish(
    venueId: string,
    week: readonly OpeningHoursDay[],
  ): Observable<{ text: string; error: boolean }> {
    return this.pricing.prices(venueId).pipe(
      switchMap((prices) => {
        const covered = pricesCovering(prices?.bands ?? [], week);
        const priced =
          covered && covered.added > 0
            ? this.pricing.setPrices(venueId, covered.bands).pipe(map(() => covered.added))
            : of(0);
        return priced.pipe(
          switchMap((added) =>
            this.courts.setOpeningHours(venueId, this.from(), week).pipe(map(() => added)),
          ),
        );
      }),
      map((added) => ({
        text:
          added > 0
            ? this.i18n.t('hours.savedPriced').replace('{n}', String(added))
            : this.i18n.t('hours.saved'),
        error: false,
      })),
      catchError((failure: unknown) => of({ text: this.i18n.t(errorKey(failure)), error: true })),
    );
  }

  private edit(day: Weekday, change: Partial<OpeningHoursDay>): void {
    this.week.update((week) =>
      (week ?? []).map((one) => (one.day === day ? { ...one, ...change } : one)),
    );
    this.result.set([]);
  }

  private load(venueId: string): void {
    this.week.set(null);
    this.loadError.set(null);
    this.courts.openingHours(venueId).subscribe({
      next: (schedules) => {
        this.week.set(latestWeek(schedules));
        // A week already dated ahead is the one being edited, from its own date.
        const ahead = schedules
          .map((one) => one.effectiveFrom)
          .filter((date) => date > this.today)
          .sort()
          .pop();
        this.from.set(ahead ?? this.today);
      },
      error: (failure: unknown) => this.loadError.set(errorKey(failure)),
    });
  }
}
