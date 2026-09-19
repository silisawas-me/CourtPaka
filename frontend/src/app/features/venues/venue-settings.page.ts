import { Component, computed, effect, inject, input, signal } from '@angular/core';
import {
  FormBuilder,
  FormControl,
  FormGroup,
  ReactiveFormsModule,
  Validators,
} from '@angular/forms';
import { RouterLink } from '@angular/router';
import { forkJoin } from 'rxjs';
import { errorKey } from '../../core/http/api-error';
import { TranslationService } from '../../core/i18n/translation.service';
import {
  Court,
  CourtService,
  OpeningHours,
  OpeningHoursDay,
  WEEKDAYS,
  Weekday,
} from '../../core/venues/court.service';
import { Venue, VenueService } from '../../core/venues/venue.service';
import { FieldError } from '../../shared/field-error';

/** A venue opens on the hour: 0 is the start of the day, 24 is midnight at the end of it. */
const OPENING_HOURS = Array.from({ length: 24 }, (_, hour) => hour);
const CLOSING_HOURS = Array.from({ length: 24 }, (_, hour) => hour + 1);

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
  imports: [ReactiveFormsModule, RouterLink, FieldError],
  templateUrl: './venue-settings.page.html',
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
  protected readonly savingCourt = signal<string | null>(null);
  protected readonly renaming = signal<string | null>(null);
  protected readonly addingCourt = signal(false);
  protected readonly savingHours = signal(false);

  /** ManageSettings is what this page is for, and a frozen venue refuses every write (PRD US-20). */
  protected readonly canManage = computed(() => {
    const venue = this.venue();
    return (
      venue !== null && venue.permissions.includes('ManageSettings') && venue.status === 'Approved'
    );
  });

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

  protected readonly hoursForm = this.formBuilder.nonNullable.group({
    effectiveFrom: ['', Validators.required],
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
    if (this.renameForm.invalid || this.savingCourt() !== null) {
      return;
    }

    this.savingCourt.set(court.id);
    this.courtError.set(null);

    this.courts
      .updateCourt(this.venueId(), court.id, this.renameForm.controls.name.value, court.position)
      .subscribe({
        next: (updated) => {
          this.savingCourt.set(null);
          this.renaming.set(null);
          this.patchCourt(court.id, updated);
        },
        error: (error: unknown) => {
          this.savingCourt.set(null);
          this.courtError.set(errorKey(error));
        },
      });
  }

  protected toggleCourt(court: Court, active: boolean): void {
    if (this.savingCourt() === court.id) {
      return;
    }

    this.savingCourt.set(court.id);
    this.courtError.set(null);
    // Show it straight away; putting it back on refusal is what resets the checkbox.
    this.patchCourt(court.id, { isActive: active });

    this.courts.changeCourtStatus(this.venueId(), court.id, active).subscribe({
      next: (updated) => {
        this.savingCourt.set(null);
        this.patchCourt(court.id, updated);
      },
      error: (error: unknown) => {
        this.savingCourt.set(null);
        this.patchCourt(court.id, { isActive: court.isActive });
        this.courtError.set(errorKey(error));
      },
    });
  }

  protected saveHours(): void {
    this.hoursForm.markAllAsTouched();
    if (this.hoursForm.invalid || this.savingHours()) {
      return;
    }

    this.savingHours.set(true);
    this.hoursError.set(null);
    const { effectiveFrom } = this.hoursForm.getRawValue();

    this.courts.setOpeningHours(this.venueId(), effectiveFrom, this.week()).subscribe({
      next: (saved) => {
        this.savingHours.set(false);
        // The server keeps every version but only ever runs on the newest for a date, so the page
        // shows one row per date, like the list endpoint does.
        this.schedules.update((list) => [
          ...list.filter((week) => week.effectiveFrom !== saved.effectiveFrom),
          saved,
        ]);
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
}
