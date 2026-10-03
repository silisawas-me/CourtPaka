import { Component, computed, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, FormGroupDirective, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { catchError, map, of, Subject, Subscription, switchMap } from 'rxjs';
import {
  AdminService,
  Complaint,
  ComplaintChannel,
  ComplaintStatus,
  ComplaintSummary,
} from '../../core/admin/admin.service';
import { errorKey } from '../../core/http/api-error';
import { AppDateTimePipe } from '../../core/i18n/app-date.pipe';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import { FieldError } from '../../shared/field-error';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { AdminTabs } from './admin-tabs';

/** Complaint.DetailsMaxLength and ResolutionMaxLength. */
const TEXT_MAX_LENGTH = 2000;

const CHANNELS: ComplaintChannel[] = ['Email', 'Phone', 'Line', 'Other'];
const FILTERS: (ComplaintStatus | 'All')[] = ['Open', 'Resolved', 'All'];

/**
 * Complaints about bookings (PRD US-22): opened against one booking, worked with that booking's
 * whole history on screen, and closed with what was done.
 *
 * The slip is a bank account (PDPA, PRD 8). It is never loaded with the page: it is fetched when
 * an admin presses for it, the server records the look before it answers, and the list of looks
 * sits under the button so the person pressing can see that it is recorded.
 */
@Component({
  selector: 'app-admin-complaints-page',
  imports: [
    AdminTabs,
    ReactiveFormsModule,
    FieldError,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    AppDateTimePipe,
    BahtPipe,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './complaints.page.html',
  styleUrl: './complaints.page.scss',
})
export class AdminComplaintsPage implements OnInit {
  private readonly admin = inject(AdminService);
  private readonly forms = inject(FormBuilder);
  private readonly destroyed = inject(DestroyRef);

  protected readonly i18n = inject(TranslationService);
  protected readonly channels = CHANNELS;
  protected readonly filters = FILTERS;
  protected readonly textMaxLength = TEXT_MAX_LENGTH;

  protected readonly filter = signal<ComplaintStatus | 'All'>('Open');
  protected readonly complaints = signal<ComplaintSummary[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  protected readonly openForm = this.forms.group({
    bookingId: this.forms.nonNullable.control('', Validators.required),
    // No default: how it came in is a fact to record, not a choice to accept.
    channel: this.forms.control<ComplaintChannel | null>(null, Validators.required),
    details: this.forms.nonNullable.control('', [
      Validators.required,
      Validators.maxLength(TEXT_MAX_LENGTH),
    ]),
  });
  protected readonly opening = signal(false);
  protected readonly openError = signal<string | null>(null);

  protected readonly current = signal<Complaint | null>(null);
  protected readonly currentError = signal<string | null>(null);
  private readonly showing = new Subject<string>();

  protected readonly resolveForm = this.forms.nonNullable.group({
    resolution: ['', [Validators.required, Validators.maxLength(TEXT_MAX_LENGTH)]],
  });
  protected readonly resolving = signal(false);
  protected readonly resolveError = signal<string | null>(null);

  protected readonly slipUrl = signal<string | null>(null);
  protected readonly slipIsPdf = signal(false);
  protected readonly slipError = signal<string | null>(null);
  protected readonly fetchingSlip = signal(false);

  /** The slip request in flight, so leaving the complaint (or the page) can call it off. */
  private slipRequest: Subscription | null = null;

  /** Lists asked for, newest last; switchMap drops an older answer that arrives late. */
  private readonly listing = new Subject<ComplaintStatus | 'All'>();

  constructor() {
    // switchMap, so a complaint opened earlier cannot land on top of the one asked for last.
    this.showing
      .pipe(
        switchMap((id) =>
          this.admin.complaint(id).pipe(
            map((complaint) => ({ complaint, failure: null as unknown })),
            catchError((failure: unknown) => of({ complaint: null, failure })),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ complaint, failure }) => {
        if (complaint) {
          this.current.set(complaint);
        } else {
          this.currentError.set(errorKey(failure));
        }
      });

    this.listing
      .pipe(
        switchMap((filter) =>
          this.admin.complaints(filter === 'All' ? undefined : filter).pipe(
            map((list) => ({ list, failure: null as unknown })),
            catchError((failure: unknown) => of({ list: null, failure })),
          ),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ list, failure }) => {
        this.loading.set(false);
        if (list) {
          this.complaints.set(list);
        } else {
          this.pageError.set(errorKey(failure));
        }
      });

    // A blob URL is held by the browser until it is told to let go, and a request still in
    // flight would make one after the page is gone.
    this.destroyed.onDestroy(() => this.releaseSlip());
  }

  ngOnInit(): void {
    this.load();
  }

  protected show(filter: ComplaintStatus | 'All'): void {
    this.filter.set(filter);
    this.load();
  }

  protected select(id: string): void {
    if (this.current()?.id === id) {
      this.close();
      return;
    }

    this.close();
    this.showing.next(id);
  }

  protected open(form: FormGroupDirective): void {
    this.openForm.markAllAsTouched();
    if (this.openForm.invalid || this.opening()) {
      return;
    }

    const { bookingId, channel, details } = this.openForm.getRawValue();
    this.opening.set(true);
    this.openError.set(null);

    this.admin.openComplaint(bookingId.trim(), details.trim(), channel!).subscribe({
      next: (complaint) => {
        this.opening.set(false);
        form.resetForm();
        this.close();
        this.current.set(complaint);
        this.load();
      },
      error: (failure: unknown) => {
        this.opening.set(false);
        this.openError.set(errorKey(failure));
      },
    });
  }

  /** Fetches the slip on purpose, and reads the complaint again so the new look is listed. */
  protected seeSlip(complaint: Complaint): void {
    if (this.fetchingSlip()) {
      return;
    }

    this.releaseSlip();
    this.fetchingSlip.set(true);
    this.slipRequest = this.admin.complaintSlip(complaint.id).subscribe({
      next: (slip) => {
        this.fetchingSlip.set(false);
        // Somebody's bank account: only ever shown under the complaint it was asked for.
        if (this.current()?.id !== complaint.id) {
          return;
        }
        this.slipIsPdf.set(slip.type === 'application/pdf');
        this.slipUrl.set(URL.createObjectURL(slip));
        this.showing.next(complaint.id);
      },
      error: (failure: unknown) => {
        this.fetchingSlip.set(false);
        this.slipError.set(errorKey(failure));
        // Refused because the complaint closed meanwhile: show it as it now is.
        this.showing.next(complaint.id);
      },
    });
  }

  protected resolve(complaint: Complaint, form: FormGroupDirective): void {
    this.resolveForm.markAllAsTouched();
    if (this.resolveForm.invalid || this.resolving()) {
      return;
    }

    this.resolving.set(true);
    this.resolveError.set(null);
    this.admin
      .resolveComplaint(complaint.id, this.resolveForm.getRawValue().resolution.trim())
      .subscribe({
        next: (after) => {
          this.resolving.set(false);
          form.resetForm();
          // Resolved, the reason to look at the slip is gone; so is the slip on screen.
          this.releaseSlip();
          this.current.set(after);
          this.load();
        },
        error: (failure: unknown) => {
          this.resolving.set(false);
          this.resolveError.set(errorKey(failure));
        },
      });
  }

  private close(): void {
    this.releaseSlip();
    this.current.set(null);
    this.currentError.set(null);
    this.resolveError.set(null);
    this.resolveForm.reset();
  }

  private releaseSlip(): void {
    this.slipRequest?.unsubscribe();
    this.slipRequest = null;
    this.fetchingSlip.set(false);

    const url = this.slipUrl();
    if (url) {
      URL.revokeObjectURL(url);
    }
    this.slipUrl.set(null);
    this.slipError.set(null);
  }

  private load(): void {
    this.loading.set(true);
    this.pageError.set(null);
    this.listing.next(this.filter());
  }
}
