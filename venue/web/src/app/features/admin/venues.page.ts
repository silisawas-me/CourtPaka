import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { errorKey } from '../../core/http/api-error';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { plainDate, venueToday } from '../../core/i18n/plain-date';
import { provideLocalizedDateAdapter } from '../../shared/localized-date-adapter';
import { AppDatePipe, AppDateTimePipe } from '../../core/i18n/app-date.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import {
  AdminVenue,
  AdminVenueDetail,
  CommissionRates,
  AdminVenuesService,
  VenueDecision,
} from '../../core/venues/admin-venues.service';
import { VenueStatus } from '../../core/venues/venue.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { AdminTabs } from './admin-tabs';
import { InviteOwner } from './invite-owner';

/** As much as the platform needs to explain itself to a venue (VenueStatusChange.ReasonMaxLength). */
const REASON_MAX_LENGTH = 500;

/** The filters worth a button. Waiting is what this screen is opened for, so it comes first. */
const FILTERS: (VenueStatus | 'All')[] = ['Pending', 'Approved', 'Suspended', 'Rejected', 'All'];

/** Which decisions need the platform to say why (PRD US-20). */
const NEEDS_REASON: VenueDecision[] = ['reject', 'suspend'];

/**
 * The platform deciding which venues may trade on it (PRD US-20).
 *
 * Judging an application means reading it, so opening one shows everything the venue said about
 * itself — tax identity included — beside every decision already made about it. The decision and
 * what it is based on are on screen together; a screen that made you remember the tax number
 * from the previous page would get tax numbers wrong.
 *
 * Refusing and suspending ask for a reason in the same place they are pressed, because that
 * reason is sent to the venue verbatim and is the only thing they are told.
 */
@Component({
  selector: 'app-admin-venues-page',
  imports: [
    AdminTabs,
    InviteOwner,
    ReactiveFormsModule,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatDatepickerModule,
    AppDatePipe,
    AppDateTimePipe,
  ],
  providers: [FORM_FIELD_DEFAULTS, provideLocalizedDateAdapter()],
  templateUrl: './venues.page.html',
  styleUrl: './venues.page.scss',
})
export class AdminVenuesPage implements OnInit {
  private readonly venues = inject(AdminVenuesService);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly filters = FILTERS;
  protected readonly reasonMaxLength = REASON_MAX_LENGTH;

  protected readonly filter = signal<VenueStatus | 'All'>('Pending');
  protected readonly venues$ = signal<AdminVenue[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  /** The venue whose application is open, once it has been asked for. One at a time. */
  protected readonly open = signal<AdminVenueDetail | null>(null);
  protected readonly openId = signal<string | null>(null);

  protected readonly deciding = signal(false);
  protected readonly decideError = signal<string | null>(null);

  /** Which decision is being asked about, where it needs a reason before it can be made. */
  protected readonly asking = signal<VenueDecision | null>(null);

  protected readonly reasonForm = this.forms.nonNullable.group({
    reason: ['', [Validators.required, Validators.maxLength(REASON_MAX_LENGTH)]],
  });

  /** What the platform charges the open venue, and its history (PRD US-21). */
  protected readonly commission = signal<CommissionRates | null>(null);
  protected readonly savingRate = signal(false);
  protected readonly rateError = signal<string | null>(null);

  protected readonly rateForm = this.forms.nonNullable.group({
    percent: [null as number | null, Validators.required],
    effectiveFrom: [venueToday(), Validators.required],
  });

  protected readonly nothingHere = computed(() => !this.loading() && this.venues$().length === 0);

  ngOnInit(): void {
    this.load();
  }

  protected show(filter: VenueStatus | 'All'): void {
    this.filter.set(filter);
    this.open.set(null);
    this.openId.set(null);
    this.load();
  }

  protected openVenue(venue: AdminVenue): void {
    if (this.openId() === venue.id) {
      this.open.set(null);
      this.openId.set(null);
      return;
    }

    this.openId.set(venue.id);
    this.open.set(null);
    this.asking.set(null);
    this.decideError.set(null);
    this.commission.set(null);
    this.rateError.set(null);
    this.rateForm.reset({ percent: null, effectiveFrom: venueToday() });
    this.refresh(venue.id);
    this.readCommission(venue.id);
  }

  /**
   * Reads one application, and only draws it if it is still the one open. Without that check a
   * slow answer lands under whichever venue the reader has moved on to — and this screen is
   * where somebody reads a tax number before deciding something about it.
   */
  private refresh(venueId: string): void {
    if (this.openId() !== venueId) {
      return;
    }

    this.venues.one(venueId).subscribe({
      next: (detail) => {
        if (this.openId() === venueId) {
          this.open.set(detail);
        }
      },
      error: (failure: unknown) => this.decideError.set(errorKey(failure)),
    });
  }

  /**
   * What this venue is charged. Read when its application is opened, and checked against the
   * venue still being the open one — the same reason the application itself is (PRD US-20).
   */
  private readCommission(venueId: string): void {
    this.venues.commission(venueId).subscribe({
      next: (charged) => {
        if (this.openId() === venueId) {
          this.commission.set(charged);
        }
      },
      error: (failure: unknown) => this.rateError.set(errorKey(failure)),
    });
  }

  /**
   * Agrees a rate from a date (PRD US-21). The answer is the whole history, because the new rate
   * may not be the one in force — the platform tells a venue about a change before it starts.
   */
  protected saveRate(): void {
    const venueId = this.openId();
    const { percent, effectiveFrom } = this.rateForm.getRawValue();

    if (venueId === null || percent === null || this.savingRate()) {
      return;
    }

    this.savingRate.set(true);
    this.rateError.set(null);

    this.venues.setCommission(venueId, percent, plainDate(effectiveFrom)).subscribe({
      next: (charged) => {
        this.savingRate.set(false);
        if (this.openId() === venueId) {
          this.commission.set(charged);
          this.rateForm.reset({ percent: null, effectiveFrom: venueToday() });
        }
      },
      error: (failure: unknown) => {
        this.savingRate.set(false);
        this.rateError.set(errorKey(failure));
      },
    });
  }

  /** Whether this decision has to say why before it can be made (PRD US-20). */
  protected needsReason(decision: VenueDecision): boolean {
    return NEEDS_REASON.includes(decision);
  }

  protected ask(decision: VenueDecision): void {
    this.asking.set(decision);
    this.reasonForm.reset({ reason: '' });
    this.decideError.set(null);
  }

  protected decide(venueId: string, decision: VenueDecision): void {
    if (this.deciding()) {
      return;
    }

    let reason: string | undefined;

    if (this.needsReason(decision)) {
      this.reasonForm.markAllAsTouched();
      reason = this.reasonForm.getRawValue().reason.trim();

      if (this.reasonForm.invalid || reason.length === 0) {
        return;
      }
    }

    this.deciding.set(true);
    this.decideError.set(null);

    this.venues.decide(venueId, decision, reason).subscribe({
      next: (decided) => {
        this.deciding.set(false);
        this.asking.set(null);

        // The list is filtered by standing, so a venue that has just moved usually belongs
        // somewhere else. Reading it again is what says so.
        this.load();
        this.refresh(decided.id);
      },
      error: (failure: unknown) => {
        this.deciding.set(false);
        this.decideError.set(errorKey(failure));
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.pageError.set(null);

    const wanted = this.filter();
    this.venues.list(wanted === 'All' ? undefined : wanted).subscribe({
      next: (venues) => {
        this.loading.set(false);
        this.venues$.set(venues);
      },
      error: (failure: unknown) => {
        this.loading.set(false);
        this.pageError.set(errorKey(failure));
      },
    });
  }
}
