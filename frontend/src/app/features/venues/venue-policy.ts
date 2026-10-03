import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { CancellationTier, PricingService } from '../../core/venues/pricing.service';

/** A step as typed: text until it is saved, so a half-typed number is not thrown away. */
interface TierDraft {
  hoursBefore: string;
  refundPercent: string;
}

/**
 * What a booker gets back for calling off (thai-fit "นโยบายยกเลิก"): steps of "at least N hours
 * before, back P%". Saved as a new version — bookings already made keep the policy they were made
 * under (BR-05), so the note under the button says so.
 */
@Component({
  selector: 'app-venue-policy',
  templateUrl: './venue-policy.html',
  styleUrl: './venue-settings-tab.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class VenuePolicy {
  private readonly pricing = inject(PricingService);
  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  readonly canManage = input(false);

  protected readonly tiers = signal<TierDraft[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly saved = signal(false);
  protected readonly busy = signal(false);

  /** The steps as numbers, most generous first — what the booker will read. */
  protected readonly parsed = computed<CancellationTier[]>(() =>
    this.tiers()
      .map((one) => ({
        hoursBefore: Number(one.hoursBefore),
        refundPercent: Number(one.refundPercent),
      }))
      .filter((one) => Number.isFinite(one.hoursBefore) && Number.isFinite(one.refundPercent))
      .sort((a, b) => b.hoursBefore - a.hoursBefore),
  );

  /** The last step's hours: under it nothing comes back. */
  protected readonly floor = computed(() => this.parsed().at(-1)?.hoursBefore ?? null);

  constructor() {
    effect(() => {
      this.pricing.cancellationPolicy(this.venueId()).subscribe({
        next: (policy) =>
          this.tiers.set(
            policy.tiers.map((one) => ({
              hoursBefore: String(one.hoursBefore),
              refundPercent: String(one.refundPercent),
            })),
          ),
        error: () => this.tiers.set([]),
      });
    });
  }

  protected change(index: number, field: keyof TierDraft, value: string): void {
    this.saved.set(false);
    this.tiers.update((all) =>
      all.map((one, i) => (i === index ? { ...one, [field]: value } : one)),
    );
  }

  protected addStep(): void {
    this.saved.set(false);
    this.tiers.update((all) => [...all, { hoursBefore: '12', refundPercent: '25' }]);
  }

  protected remove(index: number): void {
    this.saved.set(false);
    this.tiers.update((all) => all.filter((_, i) => i !== index));
  }

  protected save(): void {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.pricing.setCancellationPolicy(this.venueId(), this.parsed()).subscribe({
      next: () => {
        this.busy.set(false);
        this.saved.set(true);
      },
      error: (failure: unknown) => {
        this.busy.set(false);
        this.error.set(errorKey(failure));
      },
    });
  }
}
