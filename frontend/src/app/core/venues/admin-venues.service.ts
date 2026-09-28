import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { VenueBusiness, VenueStatus } from './venue.service';

/** A venue as the platform's own screen lists it (PRD US-20). */
export interface AdminVenue {
  id: string;
  code: string;
  name: string;
  addressLine: string;
  district: string;
  province: string;
  status: VenueStatus;
  createdAt: string;
}

/** One move a venue's standing made (PRD US-20). */
export interface VenueStatusChange {
  from: VenueStatus | null;
  to: VenueStatus;
  changedAt: string;
  reason: string | null;
}

/** Everything needed to judge one application, on one answer (PRD US-20). */
export interface AdminVenueDetail {
  venue: AdminVenue;
  business: VenueBusiness;
  agreementVersion: string | null;
  agreementAcceptedAt: string | null;
  history: VenueStatusChange[];
}

/** What the platform may decide about a venue (PRD US-20). */
export type VenueDecision = 'approve' | 'reject' | 'suspend' | 'reinstate';

/** The platform deciding which venues may trade on it (PRD US-20). */
/** Where a commission invoice stands (PRD US-21). */
export type CommissionInvoiceStatus = 'Issued' | 'PaymentSubmitted' | 'Paid';

/** One booking on an invoice, as it was charged (PRD BR-08). */
export interface CommissionInvoiceLine {
  servedOn: string;
  keptBaht: number;
  percent: number;
  amountBaht: number;
}

/** One month's commission, as either side reads it (PRD US-21). */
export interface CommissionInvoice {
  id: string;
  venueId: string;
  /** Named only where the platform is reading; a venue knows whose it is. */
  venueName: string | null;
  number: string;
  month: string;
  amountBaht: number;
  status: CommissionInvoiceStatus;
  /**
   * Late, which is not a status: an invoice that is late is still waiting to be paid, and both
   * are shown at once (PRD US-21).
   */
  overdue: boolean;
  issuedAt: string;
  dueOn: string;
  submittedAt: string | null;
  hasEvidence: boolean;
  paidAt: string | null;
  refusedReason: string | null;
  /** The bookings it was worked out from. Only where a venue is reading its own. */
  lines: CommissionInvoiceLine[] | null;
}

/** One rate the platform has charged a venue, and who agreed it (PRD US-21). */
export interface CommissionRate {
  percent: number;
  effectiveFrom: string;
  setAt: string;
  /** Null once that admin's account has been closed; the rate stands either way. */
  setByEmail: string | null;
  note: string | null;
}

/**
 * What a venue is charged today, and every rate it has been charged. `todayPercent` is null
 * where the platform has never agreed one — which is not nought: nought is a rate somebody
 * chose, and null is a question nobody has answered.
 */
export interface CommissionRates {
  todayPercent: number | null;
  rates: CommissionRate[];
}

/** The platform's invitation to somebody to bring their venue on (owner-complete 3a). */
export interface OwnerInvitation {
  id: string;
  email: string;
  createdAt: string;
  expiresAt: string;
  acceptedAt: string | null;
}

@Injectable({ providedIn: 'root' })
export class AdminVenuesService {
  private readonly http = inject(HttpClient);

  ownerInvitations(): Observable<OwnerInvitation[]> {
    return this.http.get<OwnerInvitation[]>('/api/admin/owner-invitations');
  }

  inviteOwner(email: string, language: string): Observable<OwnerInvitation> {
    return this.http.post<OwnerInvitation>('/api/admin/owner-invitations', { email, language });
  }

  list(status?: VenueStatus): Observable<AdminVenue[]> {
    return this.http.get<AdminVenue[]>('/api/admin/venues', {
      params: status ? { status } : {},
    });
  }

  /** Every venue's invoices, newest month first (PRD US-21). */
  invoices(status?: CommissionInvoiceStatus): Observable<CommissionInvoice[]> {
    return this.http.get<CommissionInvoice[]>('/api/admin/commission/invoices', {
      params: status ? { status } : {},
    });
  }

  /** The platform has seen the money. */
  markPaid(invoiceId: string): Observable<CommissionInvoice> {
    return this.http.post<CommissionInvoice>(
      `/api/admin/commission/invoices/${invoiceId}/paid`,
      null,
    );
  }

  /** The platform does not agree it has been paid, and says why. */
  refusePayment(invoiceId: string, reason: string): Observable<CommissionInvoice> {
    return this.http.post<CommissionInvoice>(`/api/admin/commission/invoices/${invoiceId}/refuse`, {
      reason,
    });
  }

  one(venueId: string): Observable<AdminVenueDetail> {
    return this.http.get<AdminVenueDetail>(`/api/admin/venues/${venueId}`);
  }

  /** What this venue is charged today, and every rate it has ever been charged (PRD US-21). */
  commission(venueId: string): Observable<CommissionRates> {
    return this.http.get<CommissionRates>(`/api/admin/venues/${venueId}/commission`);
  }

  /** Agrees a rate from a date. The answer is the whole history, the newest first. */
  setCommission(
    venueId: string,
    percent: number,
    effectiveFrom: string,
    note?: string,
  ): Observable<CommissionRates> {
    return this.http.post<CommissionRates>(`/api/admin/venues/${venueId}/commission`, {
      percent,
      effectiveFrom,
      note: note ?? null,
    });
  }

  decide(venueId: string, decision: VenueDecision, reason?: string): Observable<AdminVenue> {
    return this.http.post<AdminVenue>(`/api/admin/venues/${venueId}/${decision}`, {
      reason: reason ?? null,
    });
  }
}
