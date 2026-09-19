import { Component, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { CancellationTier, PricingService } from '../../core/venues/pricing.service';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';

type TierForm = FormGroup<{
  hoursBefore: FormControl<number>;
  refundPercent: FormControl<number>;
}>;

/** One to three steps, which is what the server accepts (PRD US-11). */
const MAX_TIERS = 3;

/**
 * What a booker gets back when they cancel (PRD US-11). A booking keeps the policy it was made
 * under, so publishing a new one only changes what happens to bookings made afterwards.
 */
@Component({
  selector: 'app-cancellation-policy',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './cancellation-policy.html',
})
export class CancellationPolicyEditor {
  private readonly pricing = inject(PricingService);
  private readonly formBuilder = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly maxTiers = MAX_TIERS;

  readonly venueId = input.required<string>();
  readonly canManage = input.required<boolean>();

  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly published = signal<CancellationTier[]>([]);

  private readonly tiersArray = this.formBuilder.array<TierForm>([]);

  /** The array needs a group around it for the template to bind to. */
  protected readonly form = this.formBuilder.group({ tiers: this.tiersArray });

  protected get tiers() {
    return this.tiersArray;
  }

  constructor() {
    effect(() => this.load(this.venueId()));
  }

  protected addTier(): void {
    if (this.tiers.length < MAX_TIERS) {
      this.tiers.push(this.tierForm({ hoursBefore: 12, refundPercent: 50 }));
    }
  }

  protected removeTier(index: number): void {
    this.tiers.removeAt(index);
  }

  protected save(): void {
    if (this.saving() || this.tiers.length === 0) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);

    this.pricing.setCancellationPolicy(this.venueId(), this.tiers.getRawValue()).subscribe({
      next: (policy) => {
        this.saving.set(false);
        this.published.set(policy.tiers);
      },
      error: (failure: unknown) => {
        this.saving.set(false);
        this.error.set(errorKey(failure));
      },
    });
  }

  private tierForm(tier: CancellationTier): TierForm {
    return this.formBuilder.nonNullable.group({
      hoursBefore: this.formBuilder.nonNullable.control(tier.hoursBefore),
      refundPercent: this.formBuilder.nonNullable.control(tier.refundPercent),
    });
  }

  private load(venueId: string): void {
    this.loading.set(true);
    this.error.set(null);
    this.tiers.clear();

    this.pricing.cancellationPolicy(venueId).subscribe({
      next: (policy) => {
        this.loading.set(false);
        this.published.set(policy.tiers);
        // A venue that never set one gets the default back, which is what editing starts from.
        for (const tier of policy.tiers) {
          this.tiers.push(this.tierForm(tier));
        }
      },
      error: (failure: unknown) => {
        this.loading.set(false);
        this.error.set(errorKey(failure));
      },
    });
  }
}
