import { Component, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { catchError, map, of, switchMap, tap } from 'rxjs';
import { ApiError, errorKey } from '../../core/http/api-error';
import { AppDatePipe, AppDateTimePipe } from '../../core/i18n/app-date.pipe';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { fromPlainDate, plainDate, VENUE_TIME_ZONE, venueToday } from '../../core/i18n/plain-date';
import { TranslationService } from '../../core/i18n/translation.service';
import { DayMoney, VenueBookingsService } from '../../core/venues/venue-bookings.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { provideLocalizedDateAdapter } from '../../shared/localized-date-adapter';

/** As much as may be written against a count, and as much as the column holds. */
const NOTE_MAX_LENGTH = 400;

/**
 * A day's money and the count at the end of it (PRD US-26). What came in, in what form, and then
 * the one thing this page exists for: the cash the till should hold against the cash in it.
 *
 * The day lives in the URL like the booker's grid keeps its day there, so the count somebody
 * disputes in the morning is a link. The arithmetic is the server's — the page only says what
 * was counted, because a difference the screen worked out is a difference nobody can check.
 */
@Component({
  selector: 'app-money-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    AppDatePipe,
    AppDateTimePipe,
    BahtPipe,
  ],
  providers: [FORM_FIELD_DEFAULTS, provideLocalizedDateAdapter()],
  templateUrl: './money.page.html',
  styleUrl: './money.page.scss',
})
export class MoneyPage {
  private readonly bookings = inject(VenueBookingsService);
  private readonly router = inject(Router);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly noteMaxLength = NOTE_MAX_LENGTH;

  readonly venueId = input.required<string>();
  readonly date = input<string>();

  /** A day that has not happened cannot be counted, so the calendar does not offer one. */
  protected readonly today = venueToday();

  protected readonly money = signal<DayMoney | null>(null);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  protected readonly closing = signal(false);
  protected readonly closeError = signal<string | null>(null);

  protected readonly dayField = this.forms.control(venueToday());

  /**
   * The count. The float is asked for rather than remembered: what was in the till this morning
   * is something the person closing knows and the system does not.
   */
  protected readonly closeForm = this.forms.group({
    openingFloatBaht: this.forms.control<number | null>(null, [
      Validators.required,
      Validators.min(0),
    ]),
    countedCashBaht: this.forms.control<number | null>(null, [
      Validators.required,
      Validators.min(0),
    ]),
    note: this.forms.nonNullable.control('', Validators.maxLength(NOTE_MAX_LENGTH)),
  });

  private readonly times = computed(
    () =>
      new Intl.DateTimeFormat(this.i18n.locale(), {
        hour: '2-digit',
        minute: '2-digit',
        timeZone: VENUE_TIME_ZONE,
      }),
  );

  /** Bumped when the day has been counted, because that changes what the server says about it. */
  private readonly counted = signal(0);

  /** The day being asked about: the URL's, or the venue's own today (PRD BR-10). */
  private readonly asked = computed(() => ({
    venueId: this.venueId(),
    date: this.date() ?? plainDate(venueToday()),
    counted: this.counted(),
  }));

  constructor() {
    toObservable(this.asked)
      .pipe(
        tap(({ date }) => {
          this.loading.set(true);
          this.pageError.set(null);
          this.closeError.set(null);
          this.dayField.setValue(fromPlainDate(date), { emitEvent: false });
        }),
        // switchMap, so stepping back two days quickly cannot let the older answer land last
        // under a URL that says the newer one.
        switchMap(({ venueId, date }) =>
          this.bookings.money(venueId, date).pipe(
            map((money) => ({ money, failure: null as unknown })),
            catchError((failure: unknown) => of({ money: null, failure })),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ money, failure }) => {
        this.loading.set(false);
        if (money) {
          this.show(money);
        } else {
          // A plain 403 carries no code: the policy refuses before any handler speaks. For a
          // member of a working venue it means they lack ViewReports (PRD US-14, US-26).
          this.pageError.set(
            failure instanceof ApiError && failure.status === 403
              ? 'money.noPermission'
              : errorKey(failure),
          );
        }
      });
  }

  /** The hour a receipt was taken, in the venue's own time rather than the reader's. */
  protected time(at: string): string {
    return this.times().format(new Date(at));
  }

  protected pick(chosen: Date | null): void {
    if (chosen === null) {
      return;
    }

    void this.router.navigate([], {
      queryParams: { date: plainDate(chosen) },
      queryParamsHandling: 'merge',
    });
  }

  protected close(): void {
    this.closeForm.markAllAsTouched();
    if (this.closeForm.invalid || this.closing()) {
      return;
    }

    const { openingFloatBaht, countedCashBaht, note } = this.closeForm.getRawValue();
    this.closing.set(true);
    this.closeError.set(null);

    this.bookings
      .closeDay(
        this.venueId(),
        this.asked().date,
        openingFloatBaht!,
        countedCashBaht!,
        note.trim() || undefined,
      )
      .subscribe({
        next: () => {
          this.closing.set(false);
          // The day is read again rather than patched: counting it is what makes the server
          // work out which rows would explain the difference (PRD US-26), and those come with
          // the day, not with the answer to the count.
          this.counted.update((times) => times + 1);
        },
        error: (failure: unknown) => {
          this.closing.set(false);
          this.closeError.set(errorKey(failure));
        },
      });
  }

  private show(money: DayMoney): void {
    this.money.set(money);
    this.closeForm.reset({ openingFloatBaht: null, countedCashBaht: null, note: '' });
  }
}
