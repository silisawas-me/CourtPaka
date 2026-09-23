import { Component, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router } from '@angular/router';
import { catchError, map, of, switchMap, tap } from 'rxjs';
import { AdminService, PlatformDashboard } from '../../core/admin/admin.service';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { fromPlainDate, plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { VenueStatus } from '../../core/venues/venue.service';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { provideLocalizedDateAdapter } from '../../shared/localized-date-adapter';
import { AdminTabs } from './admin-tabs';

const STATUSES: VenueStatus[] = ['Pending', 'Approved', 'Suspended', 'Rejected'];

/**
 * The platform's figures for a range (PRD US-22): how many bookings each venue sold, through
 * which channel, and the GMV the platform's commission is later worked out from. Counted the way
 * a venue counts its own (US-15), by the day played. The range lives in the URL, like the
 * venue's own dashboard.
 */
@Component({
  selector: 'app-admin-dashboard-page',
  imports: [
    AdminTabs,
    ReactiveFormsModule,
    MatButtonModule,
    MatCardModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    AppDatePipe,
    BahtPipe,
  ],
  providers: [FORM_FIELD_DEFAULTS, provideLocalizedDateAdapter()],
  templateUrl: './dashboard.page.html',
  styleUrl: './dashboard.page.scss',
})
export class AdminDashboardPage {
  private readonly admin = inject(AdminService);
  private readonly router = inject(Router);

  protected readonly i18n = inject(TranslationService);
  protected readonly statuses = STATUSES;

  readonly from = input<string>();
  readonly to = input<string>();

  protected readonly figures = signal<PlatformDashboard | null>(null);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  protected readonly range = new FormGroup({
    start: new FormControl<Date | null>(null),
    end: new FormControl<Date | null>(null),
  });

  private readonly asked = computed(() => ({ from: this.from(), to: this.to() }));

  constructor() {
    // switchMap, so a slower answer to an older range cannot land on top of a newer one.
    toObservable(this.asked)
      .pipe(
        tap(() => {
          this.loading.set(true);
          this.pageError.set(null);
        }),
        switchMap(({ from, to }) =>
          this.admin.dashboard(from, to).pipe(
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
          this.range.setValue(
            { start: fromPlainDate(figures.from), end: fromPlainDate(figures.to) },
            { emitEvent: false },
          );
        } else {
          this.pageError.set(errorKey(failure));
        }
      });
  }

  protected picked(): void {
    const { start, end } = this.range.getRawValue();
    if (start && end) {
      this.show(plainDate(start), plainDate(end));
    }
  }

  protected thisMonth(): void {
    const today = venueToday();
    this.show(
      plainDate(new Date(today.getFullYear(), today.getMonth(), 1)),
      plainDate(new Date(today.getFullYear(), today.getMonth() + 1, 0)),
    );
  }

  protected lastMonth(): void {
    const today = venueToday();
    this.show(
      plainDate(new Date(today.getFullYear(), today.getMonth() - 1, 1)),
      plainDate(new Date(today.getFullYear(), today.getMonth(), 0)),
    );
  }

  private show(from: string, to: string): void {
    void this.router.navigate([], { queryParams: { from, to }, queryParamsHandling: 'merge' });
  }
}
