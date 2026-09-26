import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import {
  AdminVenuesService,
  CommissionInvoice,
  CommissionInvoiceStatus,
} from '../../core/venues/admin-venues.service';
import { errorKey } from '../../core/http/api-error';
import { AppDatePipe, AppDateTimePipe } from '../../core/i18n/app-date.pipe';
import { BahtPipe } from '../../core/i18n/baht.pipe';
import { TranslationService } from '../../core/i18n/translation.service';
import { FORM_FIELD_DEFAULTS } from '../../shared/form-field-defaults';
import { AdminTabs } from './admin-tabs';

const FILTERS: (CommissionInvoiceStatus | 'All')[] = ['Issued', 'PaymentSubmitted', 'Paid', 'All'];

/**
 * What the platform has billed, and what it is still owed (PRD US-21).
 *
 * The list leads with what is waiting to be checked and what is late, because those are the only
 * two things anybody opens this screen to do something about. A paid invoice is here to be found,
 * not to be read.
 */
@Component({
  selector: 'app-admin-commission-page',
  imports: [
    AdminTabs,
    ReactiveFormsModule,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    AppDatePipe,
    AppDateTimePipe,
    BahtPipe,
  ],
  providers: [FORM_FIELD_DEFAULTS],
  templateUrl: './commission.page.html',
  styleUrl: './commission.page.scss',
})
export class AdminCommissionPage implements OnInit {
  private readonly venues = inject(AdminVenuesService);
  private readonly forms = inject(FormBuilder);

  protected readonly i18n = inject(TranslationService);
  protected readonly filters = FILTERS;

  protected readonly filter = signal<CommissionInvoiceStatus | 'All'>('PaymentSubmitted');
  protected readonly invoices = signal<CommissionInvoice[]>([]);
  protected readonly loading = signal(true);
  protected readonly pageError = signal<string | null>(null);

  /** Which invoice is being sent back, where one is. One at a time. */
  protected readonly refusing = signal<string | null>(null);
  protected readonly deciding = signal(false);
  protected readonly decideError = signal<string | null>(null);

  protected readonly refuseForm = this.forms.nonNullable.group({ reason: [''] });

  protected readonly nothingHere = computed(() => !this.loading() && this.invoices().length === 0);

  ngOnInit(): void {
    this.load();
  }

  protected show(filter: CommissionInvoiceStatus | 'All'): void {
    this.filter.set(filter);
    this.refusing.set(null);
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.pageError.set(null);

    const wanted = this.filter();
    this.venues.invoices(wanted === 'All' ? undefined : wanted).subscribe({
      next: (invoices) => {
        this.loading.set(false);
        this.invoices.set(invoices);
      },
      error: (failure: unknown) => {
        this.loading.set(false);
        this.pageError.set(errorKey(failure));
      },
    });
  }

  protected askRefuse(invoiceId: string): void {
    this.refusing.set(invoiceId);
    this.refuseForm.reset({ reason: '' });
    this.decideError.set(null);
  }

  protected close(): void {
    this.refusing.set(null);
    this.decideError.set(null);
  }

  protected markPaid(invoice: CommissionInvoice): void {
    this.decide(invoice, this.venues.markPaid(invoice.id));
  }

  protected refuse(invoice: CommissionInvoice): void {
    const reason = this.refuseForm.getRawValue().reason.trim();
    if (reason.length === 0) {
      this.decideError.set('error.venue.reason_required');
      return;
    }

    this.decide(invoice, this.venues.refusePayment(invoice.id, reason));
  }

  /**
   * A decision comes back with the invoice it was about, and that row is replaced rather than
   * the list reloaded — except that the list is filtered by status, and a decision changes the
   * status, so a row that no longer belongs is dropped out of it.
   */
  private decide(
    invoice: CommissionInvoice,
    decision: ReturnType<typeof this.venues.markPaid>,
  ): void {
    if (this.deciding()) {
      return;
    }

    this.deciding.set(true);
    this.decideError.set(null);

    decision.subscribe({
      next: (decided) => {
        this.deciding.set(false);
        this.close();

        const filter = this.filter();
        this.invoices.update((all) =>
          filter !== 'All' && decided.status !== filter
            ? all.filter((one) => one.id !== decided.id)
            : all.map((one) => (one.id === decided.id ? decided : one)),
        );
      },
      error: (failure: unknown) => {
        this.deciding.set(false);
        this.decideError.set(errorKey(failure));
      },
    });
  }

  /** Where the platform looks at what the venue sent. Opened, never drawn into the page. */
  protected evidenceLink(invoice: CommissionInvoice): string {
    return `/api/admin/commission/invoices/${invoice.id}/evidence`;
  }
}
