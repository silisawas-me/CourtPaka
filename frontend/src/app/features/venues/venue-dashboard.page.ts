import { Component, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { catchError, map, of, switchMap, tap } from 'rxjs';
import { ApiError, errorKey } from '../../core/http/api-error';
import { plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { Dashboard, VenueDashboardService } from '../../core/venues/venue-dashboard.service';
import { CloseDrawer } from './close-drawer';
import { RevenuePanel } from './revenue-panel';

/**
 * The owner app's revenue section (PR-5, PRD US-15): the panel over the last fourteen days, and
 * beside it the drawer's count by shift (thai-fit T2, "ปิดยอด").
 */
@Component({
  selector: 'app-venue-dashboard-page',
  imports: [RevenuePanel, CloseDrawer, MatCardModule, MatProgressBarModule],
  templateUrl: './venue-dashboard.page.html',
  styleUrl: './venue-dashboard.page.scss',
})
export class VenueDashboardPage {
  private readonly dashboards = inject(VenueDashboardService);

  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();

  /** Which half is on screen: the fortnight or the drawer's count. */
  protected readonly tab = signal<'overview' | 'close'>('overview');
  protected readonly tabs = ['overview', 'close'] as const;

  protected readonly figures = signal<Dashboard | null>(null);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  /** The fortnight to today in the venue's own time. */
  private readonly asked = computed(() => {
    const today = venueToday();
    return {
      venueId: this.venueId(),
      from: plainDate(new Date(today.getFullYear(), today.getMonth(), today.getDate() - 13)),
      to: plainDate(today),
    };
  });

  constructor() {
    // switchMap, so moving between branches quickly cannot let the slower answer land last.
    toObservable(this.asked)
      .pipe(
        tap(() => {
          this.loading.set(true);
          this.pageError.set(null);
        }),
        switchMap(({ venueId, from, to }) =>
          this.dashboards.read(venueId, from, to).pipe(
            map((figures) => ({ figures, failure: null as unknown })),
            catchError((failure: unknown) => of({ figures: null, failure })),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ figures, failure }) => {
        this.loading.set(false);
        if (figures) {
          this.figures.set(figures);
        } else {
          this.fail(failure);
        }
      });
  }

  private fail(failure: unknown): void {
    // A plain 403 carries no code: the policy refuses before any handler speaks. For a member
    // of a working venue it means they lack ViewReports, which is worth saying in words (US-14).
    this.pageError.set(
      failure instanceof ApiError && failure.status === 403
        ? 'dashboard.noPermission'
        : errorKey(failure),
    );
  }
}
