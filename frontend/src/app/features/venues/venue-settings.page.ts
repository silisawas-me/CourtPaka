import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import {
  FormBuilder,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  Validators,
} from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { AppDatePipe } from '../../core/i18n/app-date.pipe';
import { plainDate, venueToday } from '../../core/i18n/plain-date';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import {
  CLOSING_HOURS,
  Court,
  CourtService,
  OPENING_HOURS,
  CourtStatusChange,
  OpeningHours,
  OpeningHoursDay,
  WEEKDAYS,
  Weekday,
} from '../../core/venues/court.service';
import { Venue, VenueService } from '../../core/venues/venue.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { provideLocalizedDateAdapter } from '../../shared/localized-date-adapter';
import { CancellationPolicyEditor } from './cancellation-policy';
import { PriceBands } from './price-bands';

/** What the form offers before a venue says otherwise: a common Thai badminton day. */
const DEFAULT_OPENS_HOUR = 6;
const DEFAULT_CLOSES_HOUR = 22;

type DayForm = FormGroup<{
  open: FormControl<boolean>;
  opensHour: FormControl<number>;
  closesHour: FormControl<number>;
}>;

@Component({
  selector: 'app-venue-settings-page',
  imports: [
    ReactiveFormsModule,
    RouterLink,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatCheckboxModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatSlideToggleModule,
    AppDatePipe,
    PriceBands,
    CancellationPolicyEditor,
  ],
  templateUrl: './venue-settings.page.html',
  providers: [FORM_FIELD_DEFAULTS, provideLocalizedDateAdapter()],
})
export class VenueSettingsPage {
  private readonly courts = inject(CourtService);
  private readonly venues = inject(VenueService);
  private readonly formBuilder = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly weekdays = WEEKDAYS;
  protected readonly openingHours = OPENING_HOURS;
  protected readonly closingHours = CLOSING_HOURS;

  readonly venueId = input.required<string>();

  protected readonly venue = signal<Venue | null>(null);
  protected readonly courtList = signal<Court[]>([]);
  protected readonly schedules = signal<OpeningHours[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);
  protected readonly courtError = signal<string | null>(null);
  protected readonly hoursError = signal<string | null>(null);
  /** The courts with a change in flight; one court saving must not unlock another one. */
  protected readonly savingCourts = signal<ReadonlySet<string>>(new Set());
  protected readonly scheduled = signal<Record<string, CourtStatusChange[]>>({});
  protected readonly renaming = signal<string | null>(null);
  protected readonly addingCourt = signal(false);
  protected readonly savingHours = signal(false);

  /** What this venue asks for up front, and how the last attempt to change it went (PRD US-28). */
  protected readonly depositPercent = computed(() => this.venue()?.depositPercent ?? 100);
  protected readonly savingDeposit = signal(false);
  protected readonly depositSaved = signal(false);
  protected readonly depositError = signal<string | null>(null);

  /**
   * When this venue asks somebody for more than its usual share (PRD US-28). One form saved
   * together: the thresholds only mean anything as a set.
   */
  protected readonly risk = this.formBuilder.nonNullable.group({
    on: true,
    lookbackDays: 60,
    halfAt: 2,
    fullAt: 3,
    peakFromHour: this.formBuilder.control<number | null>(null),
    peakUntilHour: this.formBuilder.control<number | null>(null),
  });

  protected readonly savingRisk = signal(false);
  protected readonly riskSaved = signal(false);
  protected readonly riskError = signal<string | null>(null);

  /**
   * A read-only venue gets a read-only form, not a form with a dead button: filling one in and
   * being refused at the end is worse than being told at the start (PRD US-20).
   */
  private readonly riskIsTheirs = effect(() => {
    const allowed = this.canManage();
    untracked(() => (allowed ? this.risk.enable() : this.risk.disable()));
  });

  /** ManageSettings is what this page is for, and a frozen venue refuses every write (PRD US-20). */
  protected readonly canManage = computed(() => {
    const venue = this.venue();
    return (
      venue !== null && venue.permissions.includes('ManageSettings') && venue.status === 'Approved'
    );
  });

  /**
   * Each court with anything dated ahead of today, so an owner is not surprised by a closure
   * someone scheduled. Computed rather than looked up per row: a fresh [] on every change
   * detection pass makes the template's loop re-diff every court, forever.
   */
  protected readonly courtRows = computed(() =>
    this.courtList().map((court) => ({ court, scheduled: this.scheduled()[court.id] ?? [] })),
  );

  protected readonly inForce = computed(
    () => this.schedules().find((week) => week.inForce) ?? null,
  );

  protected readonly upcoming = computed(() => this.schedules().filter((week) => !week.inForce));

  /** The week in force, one row per weekday in reading order, recomputed only when it changes. */
  protected readonly inForceWeek = computed(() => {
    const week = this.inForce();
    return week === null
      ? []
      : WEEKDAYS.map((day) => week.days.find((entry) => entry.day === day)).filter(
          (entry): entry is OpeningHoursDay => entry !== undefined,
        );
  });

  protected readonly newCourtForm = this.formBuilder.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(50)]],
  });

  protected readonly renameForm = this.formBuilder.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(50)]],
  });

  /**
   * The earliest date the server will take. It is the Bangkok date, not the browser's: a laptop
   * set to another zone must not be offered a date the server refuses, nor stopped from picking
   * one it would accept.
   */
  protected readonly today = signal(venueToday());

  protected readonly hoursForm = this.formBuilder.group({
    effectiveFrom: this.formBuilder.control<Date | null>(null, Validators.required),
    days: this.formBuilder.nonNullable.group(this.emptyWeek()),
  });

  constructor() {
    effect(() => this.load(this.venueId()));
  }

  protected addCourt(): void {
    this.newCourtForm.markAllAsTouched();
    if (this.newCourtForm.invalid || this.addingCourt()) {
      return;
    }

    this.addingCourt.set(true);
    this.courtError.set(null);

    this.courts.addCourt(this.venueId(), this.newCourtForm.controls.name.value).subscribe({
      next: (court) => {
        this.addingCourt.set(false);
        this.courtList.update((list) => [...list, court]);
        this.newCourtForm.reset({ name: '' });
      },
      error: (error: unknown) => {
        this.addingCourt.set(false);
        this.courtError.set(errorKey(error));
      },
    });
  }

  protected startRename(court: Court): void {
    this.renaming.set(court.id);
    this.courtError.set(null);
    this.renameForm.reset({ name: court.name });
  }

  protected cancelRename(): void {
    this.renaming.set(null);
  }

  protected saveRename(court: Court): void {
    this.renameForm.markAllAsTouched();
    if (this.renameForm.invalid || this.isSaving(court)) {
      return;
    }

    this.startSaving(court.id);
    this.courtError.set(null);

    this.courts
      .updateCourt(this.venueId(), court.id, this.renameForm.controls.name.value, court.position)
      .subscribe({
        next: (updated) => {
          this.doneSaving(court.id);
          this.renaming.set(null);
          this.patchCourt(court.id, updated);
        },
        error: (error: unknown) => {
          this.doneSaving(court.id);
          this.courtError.set(errorKey(error));
        },
      });
  }

  protected isSaving(court: Court): boolean {
    return this.savingCourts().has(court.id);
  }

  protected toggleCourt(court: Court, active: boolean): void {
    if (this.isSaving(court)) {
      return;
    }

    this.startSaving(court.id);
    this.courtError.set(null);
    // Show it straight away; putting it back on refusal is what resets the checkbox.
    this.patchCourt(court.id, { isActive: active });

    this.courts.changeCourtStatus(this.venueId(), court.id, active).subscribe({
      next: (status) => {
        this.doneSaving(court.id);
        // The server answers with its own timeline, which is not always what was asked for.
        this.patchCourt(court.id, { isActive: status.activeToday });
        this.scheduled.update((all) => ({ ...all, [court.id]: status.scheduled }));
      },
      error: (error: unknown) => {
        this.doneSaving(court.id);
        this.patchCourt(court.id, { isActive: court.isActive });
        this.courtError.set(errorKey(error));
      },
    });
  }

  private startSaving(courtId: string): void {
    this.savingCourts.update((saving) => new Set(saving).add(courtId));
  }

  private doneSaving(courtId: string): void {
    this.savingCourts.update((saving) => {
      const next = new Set(saving);
      next.delete(courtId);
      return next;
    });
  }

  protected saveHours(): void {
    this.hoursForm.markAllAsTouched();
    if (this.hoursForm.invalid || this.savingHours()) {
      return;
    }

    this.savingHours.set(true);
    this.hoursError.set(null);
    const effectiveFrom = this.hoursForm.controls.effectiveFrom.value;

    this.courts.setOpeningHours(this.venueId(), plainDate(effectiveFrom!), this.week()).subscribe({
      next: (saved) => {
        this.savingHours.set(false);
        // Mirror what the list endpoint would return: one row per start date, one week in force, in
        // date order. Without dropping the week it replaces, a week published for today would hide
        // behind the older one that is still marked as in force.
        this.schedules.update((list) =>
          [
            ...list.filter(
              (week) =>
                week.effectiveFrom !== saved.effectiveFrom && !(saved.inForce && week.inForce),
            ),
            saved,
          ].sort((left, right) => left.effectiveFrom.localeCompare(right.effectiveFrom)),
        );
      },
      error: (error: unknown) => {
        this.savingHours.set(false);
        this.hoursError.set(errorKey(error));
      },
    });
  }

  /** Puts a published week back into the form, so a small change does not mean retyping seven days. */
  protected editWeek(week: OpeningHours): void {
    for (const day of week.days) {
      this.hoursForm.controls.days.controls[day.day].setValue({
        open: day.opensHour !== null,
        opensHour: day.opensHour ?? DEFAULT_OPENS_HOUR,
        closesHour: day.closesHour ?? DEFAULT_CLOSES_HOUR,
      });
    }
  }

  private week(): OpeningHoursDay[] {
    const days = this.hoursForm.controls.days.getRawValue();
    return WEEKDAYS.map((day) => ({
      day,
      opensHour: days[day].open ? days[day].opensHour : null,
      closesHour: days[day].open ? days[day].closesHour : null,
    }));
  }

  /** Typed per weekday, so the template and editWeek address the days by name, not by index. */
  private emptyWeek(): Record<Weekday, DayForm> {
    const week = {} as Record<Weekday, DayForm>;
    for (const day of WEEKDAYS) {
      week[day] = this.formBuilder.nonNullable.group({
        open: this.formBuilder.nonNullable.control(true),
        opensHour: this.formBuilder.nonNullable.control(DEFAULT_OPENS_HOUR),
        closesHour: this.formBuilder.nonNullable.control(DEFAULT_CLOSES_HOUR),
      });
    }
    return week;
  }

  private patchCourt(courtId: string, change: Partial<Court>): void {
    this.courtList.update((list) =>
      list.map((court) => (court.id === courtId ? { ...court, ...change } : court)),
    );
  }

  private load(venueId: string): void {
    this.loading.set(true);
    this.venue.set(null);
    this.courtList.set([]);
    this.schedules.set([]);
    this.pageError.set(null);
    this.courtError.set(null);
    this.hoursError.set(null);
    this.renaming.set(null);
    this.savingCourts.set(new Set());
    this.today.set(venueToday());
    this.scheduled.set({});

    forkJoin({
      venue: this.venues.get(venueId),
      courts: this.courts.courts(venueId),
      schedules: this.courts.openingHours(venueId),
    }).subscribe({
      next: ({ venue, courts, schedules }) => {
        this.venue.set(venue);
        this.courtList.set(courts);
        this.schedules.set(schedules);
        this.loading.set(false);

        // The form shows what the venue is on, not the defaults it was built with.
        this.risk.setValue(venue.risk);

        const current = this.inForce();
        if (current) {
          this.editWeek(current);
        }
      },
      error: (error: unknown) => {
        this.pageError.set(errorKey(error));
        this.loading.set(false);
      },
    });
  }

  /**
   * Sends the rule as filled in and lets the server judge it (US-23): the page keeps no copy of
   * what counts as sensible, so it cannot disagree with the refusal it would have to translate.
   */
  protected saveRisk(): void {
    if (!this.canManage() || this.savingRisk()) {
      return;
    }

    const rule = this.risk.getRawValue();

    this.savingRisk.set(true);
    this.riskError.set(null);
    this.riskSaved.set(false);

    this.venues.setRiskRule(this.venueId(), rule).subscribe({
      next: () => {
        this.savingRisk.set(false);
        this.riskSaved.set(true);
        this.venue.update((venue) => (venue ? { ...venue, risk: rule } : venue));
      },
      error: (failure: unknown) => {
        this.savingRisk.set(false);
        this.riskError.set(errorKey(failure));
      },
    });
  }

  /**
   * The share of a price this venue asks for before it holds hours (PRD US-28). Sent as typed and
   * judged by the server: 10 to 100 is the server's rule, and a page with its own copy of it is a
   * page that can disagree with the answer (US-23).
   */
  protected chooseDeposit(event: Event): void {
    const field = event.target as HTMLInputElement;
    const percent = Number(field.value);

    this.savingDeposit.set(true);
    this.depositError.set(null);
    this.depositSaved.set(false);

    this.venues.setDeposit(this.venueId(), percent).subscribe({
      next: () => {
        this.savingDeposit.set(false);
        this.depositSaved.set(true);
        // The venue on hand is what the field reads, so it moves with what was saved.
        this.venue.update((venue) => (venue ? { ...venue, depositPercent: percent } : venue));
      },
      error: (failure: unknown) => {
        this.savingDeposit.set(false);
        this.depositError.set(errorKey(failure));
        // The field holds what was typed, and the venue is still on what it was: a refused number
        // left on screen reads as the number in force.
        field.value = String(this.depositPercent());
      },
    });
  }
}
