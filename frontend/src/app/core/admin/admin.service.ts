import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { VenueStatus } from '../venues/venue.service';

/** One venue's line on the platform dashboard (PRD US-22). */
export interface PlatformVenueFigures {
  venueId: string;
  code: string;
  name: string;
  status: VenueStatus;
  bookings: number;
  onlineBookings: number;
  staffBookings: number;
  gmvBaht: number;
}

export interface PlatformTotals {
  bookings: number;
  onlineBookings: number;
  staffBookings: number;
  gmvBaht: number;
}

/**
 * The platform's figures for a range (PRD US-22). Commission arrives with the invoices of US-21;
 * until then it is not part of the answer rather than a zero that looks like a fact.
 */
export interface PlatformDashboard {
  from: string;
  to: string;
  venuesByStatus: Record<VenueStatus, number>;
  totals: PlatformTotals;
  venues: PlatformVenueFigures[];
}

/** An account as the platform's screen lists it (PRD US-22). */
export interface AdminUser {
  id: string;
  email: string;
  emailConfirmed: boolean;
  suspendedAt: string | null;
  isPlatformAdmin: boolean;
}

export interface AccountStatusChange {
  suspended: boolean;
  reason: string;
  changedByEmail: string | null;
  changedAt: string;
}

export interface AdminUserDetail {
  user: AdminUser;
  history: AccountStatusChange[];
}

/** How a complaint reached the platform (PRD US-22). */
export type ComplaintChannel = 'Email' | 'Phone' | 'Line' | 'Other';
export type ComplaintStatus = 'Open' | 'Resolved';

export interface ComplaintSummary {
  id: string;
  bookingId: string;
  venueName: string;
  channel: ComplaintChannel;
  status: ComplaintStatus;
  openedAt: string;
  resolvedAt: string | null;
}

export interface ComplaintMove {
  from: string | null;
  to: string;
  changedAt: string;
  changedByEmail: string | null;
  cause: string | null;
  reason: string | null;
}

/** The booking a complaint is about, as much as an admin needs to judge it (PRD US-22). */
export interface ComplaintBooking {
  bookingId: string;
  venueId: string;
  venueCode: string;
  venueName: string;
  channel: 'Online' | 'Staff';
  bookerEmail: string | null;
  customerName: string | null;
  customerPhone: string | null;
  status: string;
  paymentState: string;
  totalBaht: number;
  refundDueBaht: number;
  sentBackBaht: number;
  slots: { courtName: string; startsAt: string; endsAt: string }[];
  history: ComplaintMove[];
  hasSlip: boolean;
}

export interface Complaint {
  id: string;
  details: string;
  channel: ComplaintChannel;
  status: ComplaintStatus;
  openedAt: string;
  openedByEmail: string | null;
  resolvedAt: string | null;
  resolvedByEmail: string | null;
  resolution: string | null;
  booking: ComplaintBooking;
  slipViewings: { viewedByEmail: string | null; viewedAt: string }[];
}

@Injectable({ providedIn: 'root' })
export class AdminService {
  private readonly http = inject(HttpClient);

  dashboard(from?: string, to?: string): Observable<PlatformDashboard> {
    let params = new HttpParams();
    if (from) {
      params = params.set('from', from);
    }
    if (to) {
      params = params.set('to', to);
    }
    return this.http.get<PlatformDashboard>('/api/admin/dashboard', { params });
  }

  searchUsers(query: string): Observable<AdminUser[]> {
    return this.http.get<AdminUser[]>('/api/admin/users', { params: { q: query } });
  }

  user(userId: string): Observable<AdminUserDetail> {
    return this.http.get<AdminUserDetail>(`/api/admin/users/${userId}`);
  }

  complaints(status?: ComplaintStatus): Observable<ComplaintSummary[]> {
    return this.http.get<ComplaintSummary[]>('/api/admin/complaints', {
      params: status ? { status } : {},
    });
  }

  complaint(complaintId: string): Observable<Complaint> {
    return this.http.get<Complaint>(`/api/admin/complaints/${complaintId}`);
  }

  openComplaint(
    bookingId: string,
    details: string,
    channel: ComplaintChannel,
  ): Observable<Complaint> {
    return this.http.post<Complaint>('/api/admin/complaints', { bookingId, details, channel });
  }

  resolveComplaint(complaintId: string, resolution: string): Observable<Complaint> {
    return this.http.post<Complaint>(`/api/admin/complaints/${complaintId}/resolve`, {
      resolution,
    });
  }

  /**
   * The booking's slip, through an open complaint. Every call is recorded as a look at somebody's
   * bank account (PRD US-22), so the page asks for it only when an admin presses for it.
   */
  complaintSlip(complaintId: string): Observable<Blob> {
    return this.http.get(`/api/admin/complaints/${complaintId}/slip`, { responseType: 'blob' });
  }

  /** Suspending and letting back in both say why: both are decisions about a person (PRD 8). */
  setStanding(userId: string, suspend: boolean, reason: string): Observable<AdminUserDetail> {
    return this.http.post<AdminUserDetail>(
      `/api/admin/users/${userId}/${suspend ? 'suspend' : 'reinstate'}`,
      { reason },
    );
  }
}
