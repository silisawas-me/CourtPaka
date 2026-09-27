import { Component, inject, OnInit, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import { Venue, VenueService } from '../../core/venues/venue.service';

/**
 * The venues this person belongs to, and where each of them stands (PRD US-10, US-14).
 *
 * Reading only. Applying to join moved to its own page when the application grew a tax identity
 * and a bank account: it is a form somebody fills in once, sitting down, and it has no business
 * crowding the list somebody opens every day to get to their courts.
 */
import { AllVenuesToday } from './all-venues-today';
@Component({
  selector: 'app-venues-page',
  imports: [AllVenuesToday, RouterLink, MatButtonModule, MatCardModule, MatProgressBarModule],
  templateUrl: './venues.page.html',
})
export class VenuesPage implements OnInit {
  private readonly venues = inject(VenueService);

  protected readonly i18n = inject(TranslationService);

  protected readonly mine = signal<Venue[]>([]);
  protected readonly loading = signal(true);
  protected readonly errorKey = signal<string | null>(null);

  ngOnInit(): void {
    this.venues.mine().subscribe({
      next: (venues) => {
        this.mine.set(venues);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.errorKey.set(errorKey(error));
        this.loading.set(false);
      },
    });
  }
}
