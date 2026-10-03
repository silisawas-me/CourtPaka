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
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { VenueService } from '../../core/venues/venue.service';

/** The presets the artboard offers beside − and +, in minutes (the server takes 0–60). */
const PRESETS = [0, 10, 15, 30, 60] as const;
const MAX = 60;
const STEP = 5;

/**
 * How long the venue waits for somebody late (PRD US-24), as the policy artboard's card draws it
 * ("ลูกค้ามาสาย รอได้กี่นาที"): after this many minutes past the start, "ไม่มา" opens and the
 * court can go back on sale. It changes nothing about money and never stops a check-in; it applies
 * at once to every booking, the ones already made too, because the server reads it at the door.
 */
@Component({
  selector: 'app-venue-grace',
  templateUrl: './venue-grace.html',
  styleUrls: ['./venue-settings-tab.scss', './venue-grace.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VenueGrace {
  private readonly venues = inject(VenueService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  /** What the venue waits now, from its record. */
  readonly minutes = input(15);
  readonly canManage = input(false);
  /** The page re-reads the venue, so coming back to the tab shows what was saved. */
  readonly saved$ = output<void>({ alias: 'saved' });

  protected readonly presets = PRESETS;
  protected readonly max = MAX;
  protected readonly grace = signal(15);
  protected readonly saved = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  /** "19:15": when a 19:00 booking's no-show opens, for the example beside the number. */
  protected readonly opensAt = computed(() => {
    const total = 19 * 60 + this.grace();
    return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
  });

  constructor() {
    effect(() => this.grace.set(this.minutes()));
  }

  protected nudge(by: number): void {
    this.grace.update((minutes) => Math.max(0, Math.min(MAX, minutes + by * STEP)));
    this.saved.set(false);
  }

  protected pick(minutes: number): void {
    this.grace.set(minutes);
    this.saved.set(false);
  }

  protected save(): void {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.venues.setGrace(this.venueId(), this.grace()).subscribe({
      next: () => {
        this.busy.set(false);
        this.saved.set(true);
        this.saved$.emit();
      },
      error: (failure: unknown) => {
        this.busy.set(false);
        this.error.set(errorKey(failure));
      },
    });
  }
}
