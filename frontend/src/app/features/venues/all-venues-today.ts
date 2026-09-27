import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { errorKey } from '../../core/http/api-error';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import { HourUse, OwnerToday, VenueService } from '../../core/venues/venue.service';

/**
 * Today at every venue this person reads the reports of, at a glance (badPaka 2c): what each
 * kept, how full each hour was, and what wants somebody now. Every number is the one the venue's
 * own dashboard would give — the server reads each with the dashboard's code — so this is a
 * window onto those pages, not a second set of books.
 *
 * Drawn only when there is a venue whose reports this person may read: staff who take bookings
 * but do not read the money see nothing here, the same as on the dashboard (PRD US-14).
 */
@Component({
  selector: 'app-all-venues-today',
  imports: [BahtPipe, RouterLink],
  templateUrl: './all-venues-today.html',
  styleUrl: './all-venues-today.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AllVenuesToday {
  private readonly venues = inject(VenueService);
  protected readonly i18n = inject(TranslationService);

  protected readonly today = signal<OwnerToday | null>(null);
  protected readonly error = signal<string | null>(null);

  constructor() {
    this.venues.today().subscribe({
      next: (today) => this.today.set(today),
      error: (failure: unknown) => this.error.set(errorKey(failure)),
    });
  }

  /** How full an hour was, 0–100, or null when nothing was on sale in it. */
  protected use(hour: HourUse): number | null {
    return hour.sellable === 0 ? null : Math.round((100 * hour.booked) / hour.sellable);
  }

  protected hourLabel(hour: HourUse): string {
    const use = this.use(hour);
    return use === null
      ? `${hour.hour}:00 ${this.i18n.t('overview.notOnSale')}`
      : `${hour.hour}:00 ${use}% (${hour.booked}/${hour.sellable})`;
  }
}
