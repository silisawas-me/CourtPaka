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
import { of, switchMap } from 'rxjs';
import { map } from 'rxjs/operators';
import { errorKey } from '../../core/http/api-error';
import { clockHour } from '../../core/i18n/clock.pipe';
import { plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { CourtService, OpeningHoursDay, Weekday, WEEKDAYS } from '../../core/venues/court.service';
import { PricingService } from '../../core/venues/pricing.service';
import { Venue } from '../../core/venues/venue.service';
import {
  dayIsValid,
  dayStartFor,
  LATEST_CLOSE,
  latestWeek,
  opensTooEarly,
  pricesCovering,
} from './hours-rules';

/**
 * Opening hours, as the "เวลาเปิด-ปิด" artboard of docs/plan/thai-fit.md draws them: one card, a
 * row per day of open – close, and a day may close past midnight (T4). Friday until 02:00 is
 * Friday's hours 24 and 25 — the row says "ข้ามเที่ยงคืน" and the note at the foot says what that
 * means for bookings and the drawer.
 *
 * Saving a week that opens an hour nobody has priced prices it from its nearest hour that day
 * first (`pricesCovering`): the server refuses an open hour without a price, and the price grid
 * paints only hours that are open.
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
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  readonly venue = input<Venue | null>(null);
  readonly canManage = input(false);

  /** A week was saved, so the price grid's open hours changed. */
  readonly saved = output<void>();

  protected readonly openHours = Array.from({ length: 24 }, (_, hour) => hour);
  protected readonly closeHours = Array.from({ length: LATEST_CLOSE }, (_, hour) => hour + 1);

  private readonly today = plainDate(venueToday());
  protected readonly week = signal<OpeningHoursDay[] | null>(null);
  /** The week is saved from today, or from the date of a week already published ahead. */
  private readonly from = signal(this.today);
  protected readonly loadError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly result = signal<{ text: string; error: boolean } | null>(null);

  private readonly startsNow = computed(() => this.venue()?.dayStartsHour ?? 0);

  protected readonly weekValid = computed(() => (this.week() ?? []).every(dayIsValid));
  protected readonly tooEarly = computed(() => opensTooEarly(this.week() ?? [], this.startsNow()));

  /** The artboard's example, about the first day that runs past midnight (Friday if none does). */
  protected readonly example = computed(() => {
    const late = (this.week() ?? []).find((day) => (day.closesHour ?? 0) > 24);
    const day = late?.day ?? 'Friday';
    const closes = late?.closesHour ?? 26;
    return this.i18n
      .t('hours.example')
      .replaceAll('{day}', this.dayName(day))
      .replaceAll('{from}', clockHour(closes - 1, true))
      .replaceAll('{to}', clockHour(closes, true));
  });

  constructor() {
    effect(() => this.load(this.venueId()));
  }

  protected dayName(day: Weekday): string {
    return this.i18n.t(`hours.day.${day}`);
  }

  /** "16:00"; a close at 24 is midnight, and past it the hour is the next day's (artboard). */
  protected closeLabel(day: Weekday, hour: number): string {
    if (hour < 24) {
      return clockHour(hour, true);
    }
    if (hour === 24) {
      return `24:00 ${this.i18n.t('hours.midnight')}`;
    }
    const next = WEEKDAYS[(WEEKDAYS.indexOf(day) + 1) % 7];
    return this.i18n
      .t('hours.nextDay')
      .replace('{t}', clockHour(hour, true))
      .replace('{day}', this.dayName(next));
  }

  protected openLabel(hour: number): string {
    return clockHour(hour, true);
  }

  protected late(day: OpeningHoursDay): boolean {
    return (day.closesHour ?? 0) > 24;
  }

  /** The open list carries "shut" too: a day the venue does not open is chosen where it opens. */
  protected setOpens(day: OpeningHoursDay, value: string): void {
    if (value === '') {
      this.edit(day.day, { opensHour: null, closesHour: null });
      return;
    }
    const opens = Number(value);
    this.edit(day.day, {
      opensHour: opens,
      closesHour: day.closesHour !== null && day.closesHour > opens ? day.closesHour : opens + 1,
    });
  }

  protected setCloses(day: Weekday, value: string): void {
    this.edit(day, { closesHour: Number(value) });
  }

  protected save(): void {
    const week = this.week();
    if (!week || !this.weekValid() || this.tooEarly() || this.busy()) {
      return;
    }
    this.busy.set(true);
    this.result.set(null);
    const venueId = this.venueId();
    this.pricing
      .prices(venueId)
      .pipe(
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
      )
      .subscribe({
        next: (added) => {
          this.busy.set(false);
          this.result.set({
            text:
              added > 0
                ? this.i18n.t('hours.savedPriced').replace('{n}', String(added))
                : this.i18n.t('hours.saved'),
            error: false,
          });
          this.saved.emit();
        },
        error: (failure: unknown) => {
          this.busy.set(false);
          this.result.set({ text: this.i18n.t(errorKey(failure)), error: true });
        },
      });
  }

  /** Where the day would start once saved, for the line that says why an early day is refused. */
  protected dayStart(): string {
    return clockHour(dayStartFor(this.week() ?? [], this.startsNow()), true);
  }

  private edit(day: Weekday, change: Partial<OpeningHoursDay>): void {
    this.week.update((week) =>
      (week ?? []).map((one) => (one.day === day ? { ...one, ...change } : one)),
    );
    this.result.set(null);
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
