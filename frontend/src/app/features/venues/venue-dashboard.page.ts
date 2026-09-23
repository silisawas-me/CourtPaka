import { Component, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { catchError, map, of, switchMap, tap } from 'rxjs';
import { ApiError, errorKey } from '../../core/http/api-error';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { BahtPipe, formatBaht } from '../../core/i18n/baht.pipe';
import { fromPlainDate, plainDate, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { Dashboard, VenueDashboardService } from '../../core/venues/venue-dashboard.service';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { provideLocalizedDateAdapter } from '../../shared/localized-date-adapter';

/**
 * A venue's own figures for a range of days (PRD US-15): what it kept, what is still to come,
 * how much of what it had to sell was used, and what is waiting for somebody.
 *
 * The range lives in the URL (`?from=&to=`) like the booker's grid keeps its day there, so a
 * link to last month's figures is a link to last month's figures. With neither, the server
 * answers for this month.
 */
@Component({
  selector: 'app-venue-dashboard-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
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
  templateUrl: './venue-dashboard.page.html',
  styleUrl: './venue-dashboard.page.scss',
})
export class VenueDashboardPage {
  private readonly dashboards = inject(VenueDashboardService);
  private readonly router = inject(Router);

  protected readonly i18n = inject(TranslationService);

  readonly venueId = input.required<string>();
  readonly from = input<string>();
  readonly to = input<string>();

  protected readonly figures = signal<Dashboard | null>(null);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  protected readonly range = new FormGroup({
    start: new FormControl<Date | null>(null),
    end: new FormControl<Date | null>(null),
  });

  protected readonly totalBaht = computed(() => {
    const figures = this.figures();
    return figures ? figures.onlineBaht + figures.staffBaht : 0;
  });

  /** A month table only says something the day table does not when there is more than one. */
  protected readonly manyMonths = computed(() => (this.figures()?.months.length ?? 0) > 1);

  /** What is being asked for: the venue and the range the URL holds. */
  private readonly asked = computed(() => ({
    venueId: this.venueId(),
    from: this.from(),
    to: this.to(),
  }));

  constructor() {
    // switchMap, so pressing "last month" and then "this month" quickly cannot let the slower,
    // older answer land last and show last month under a URL that says this one.
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
          this.showFigures(figures);
        } else {
          this.fail(failure);
        }
      });
  }

  /** Used out of sellable, as "12 / 32". The share alone hides how small a day was. */
  protected hours(used: number, sellable: number): string {
    const locale = this.i18n.locale();
    return `${formatBaht(used, locale)} / ${formatBaht(sellable, locale)}`;
  }

  /** "กันยายน 2569" / "September 2026": through Intl, so Thai gets the Buddhist year like dates do. */
  private readonly months = computed(
    () => new Intl.DateTimeFormat(this.i18n.locale(), { month: 'long', year: 'numeric' }),
  );

  protected monthName(year: number, month: number): string {
    return this.months().format(new Date(year, month - 1, 1));
  }

  /** Both ends are picked before anything is asked, so half a range never reaches the server. */
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
    void this.router.navigate([], {
      queryParams: { from, to },
      queryParamsHandling: 'merge',
    });
  }

  private showFigures(figures: Dashboard): void {
    this.figures.set(figures);

    // The range the server answered for, which is this month when none was asked.
    this.range.setValue(
      { start: fromPlainDate(figures.from), end: fromPlainDate(figures.to) },
      { emitEvent: false },
    );
  }

  private fail(failure: unknown): void {
    // A plain 403 carries no code: the policy refuses before any handler speaks. For a member
    // of a working venue it means they lack ViewReports, which is worth saying in words
    // (PRD US-14); a venue turned away by the platform is told so on its own page first.
    this.pageError.set(
      failure instanceof ApiError && failure.status === 403
        ? 'dashboard.noPermission'
        : errorKey(failure),
    );
  }
}
