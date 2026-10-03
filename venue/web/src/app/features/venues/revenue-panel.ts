import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import { Dashboard } from '../../core/venues/venue-dashboard.service';

/**
 * Revenue, as the owner app draws it (PR-5): three cards — the range's money and how it compares
 * with the same number of days before, court fees and their share, the shop and its best seller
 * — a bar a day split into court and shop, the money by how it was paid, and the best sellers.
 *
 * Every figure is the dashboard's own (US-15, US-32): court money is what the venue kept, the
 * shop is rung up less handed back, so this panel and the cards under it cannot disagree.
 */
@Component({
  selector: 'app-revenue-panel',
  imports: [BahtPipe],
  templateUrl: './revenue-panel.html',
  styleUrl: './revenue-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RevenuePanel {
  protected readonly i18n = inject(TranslationService);

  readonly figures = input.required<Dashboard>();
  /** The design's own page, which is always the last fourteen days and says so in its titles. */
  readonly fortnight = input(false);

  protected readonly court = computed(() => this.figures().onlineBaht + this.figures().staffBaht);
  protected readonly shop = computed(() => this.figures().trade.shopBaht);
  protected readonly total = computed(() => this.court() + this.shop());

  /** Up or down on the period before, as a whole percent; null when there was nothing before. */
  protected readonly change = computed(() => {
    const prior = this.figures().priorBaht;
    return prior > 0 ? Math.round(((this.total() - prior) / prior) * 100) : null;
  });

  protected readonly courtShare = computed(() =>
    this.total() > 0 ? Math.round((this.court() / this.total()) * 100) : null,
  );

  /** Each day's bar, as shares of the tallest day, so the chart is drawn to one scale. */
  protected readonly bars = computed(() => {
    const days = this.figures().days.map((day) => ({
      date: day.date,
      court: day.onlineBaht + day.staffBaht,
      shop: Math.max(0, day.shopBaht),
    }));
    const tallest = Math.max(1, ...days.map((day) => day.court + day.shop));
    return days.map((day) => ({
      ...day,
      label: Number(day.date.slice(8)),
      courtShare: (day.court / tallest) * 100,
      shopShare: (day.shop / tallest) * 100,
    }));
  });

  protected readonly methods = computed(() => {
    const all = this.figures().byMethod;
    const sum = all.reduce((total, one) => total + one.baht, 0);
    return all.map((one) => ({
      ...one,
      share: sum > 0 ? Math.round((one.baht / sum) * 100) : 0,
    }));
  });
}
