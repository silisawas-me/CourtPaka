import { CommissionInvoice } from './admin-venues.service';
import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export type VenueStatus = 'Pending' | 'Approved' | 'Rejected' | 'Suspended';

/** Mirrors VenuePermissionSet.Grantable on the API, which speaks these names in both directions. */
export const VENUE_PERMISSIONS = [
  'VerifySlip',
  'ManageBookings',
  'CloseCourt',
  'ViewReports',
  'ManageSettings',
] as const;

export type VenuePermission = (typeof VENUE_PERMISSIONS)[number];

/** What a newly invited staff member gets unless the owner changes it (VenuePermissions.StaffDefault). */
export const STAFF_DEFAULT_PERMISSIONS: readonly VenuePermission[] = [
  'VerifySlip',
  'ManageBookings',
  'CloseCourt',
];

/**
 * Where the money goes and who the venue is to the Revenue Department (PRD US-10). Three
 * different things travel together: the account US-04 puts in a QR, the tax identity US-07 and
 * US-16 print on documents, and an optional pin on a map.
 */
export interface VenueBusiness {
  promptPayId: string;
  promptPayAccountName: string;
  isVatRegistered: boolean;
  legalName: string;
  taxId: string;
  taxBranch: string;
  billingAddress: string;
  latitude: number | null;
  longitude: number | null;
}

/** Where a venue is, which is how a booker finds it (PRD US-02). */
export interface VenueAddress {
  addressLine: string;
  district: string;
  province: string;
}

export interface Venue extends VenueAddress {
  id: string;
  code: string;
  name: string;
  status: VenueStatus;
  /** The caller's own role and permissions at this venue, so screens never infer them. */
  role: 'Owner' | 'Staff';
  permissions: VenuePermission[];
  /** Whether this member wants to hear each time a slip arrives here (PRD US-17). */
  wantsSlipEmails: boolean;
  /**
   * How much of a booking's price this venue asks for before it holds the hours (PRD US-28).
   * A hundred is the whole of it, which is what a venue asks for until it says otherwise.
   */
  depositPercent: number;
  /** When this venue asks for more than that share, and of whom (PRD US-28). */
  risk: VenueRiskRule;
  /** How long it waits after the hour starts before nobody having come counts (US-24). */
  graceMinutes?: number;
  /**
   * The most the person reading may write down as sent back in one record (PRD US-18), or null
   * where they have no ceiling. Beside the permissions because it is one of them.
   */
  refundLimitBaht: number | null;
}

/** What a venue counts as too often, and which hours it will not lose (PRD US-28). */
export interface VenueRiskRule {
  on: boolean;
  lookbackDays: number;
  halfAt: number;
  fullAt: number;
  /** Null means the venue has named no peak, and then no hour is treated as one. */
  peakFromHour: number | null;
  peakUntilHour: number | null;
}

export interface VenueMember {
  userId: string;
  email: string;
  role: 'Owner' | 'Staff';
  permissions: VenuePermission[];
  /**
   * The most they may write down as sent back in one record (PRD US-18). Null for the owner,
   * who has no ceiling — there is nobody above them to raise one.
   */
  refundLimitBaht: number | null;
}

/** Where the platform takes its commission (PRD US-21). */
export interface PlatformAccount {
  promptPayId: string;
  accountName: string | null;
}

/**
 * What this venue owes the platform, and how to pay it. The account travels with the invoices
 * because a venue paying one is looking at it. Null where the platform has not said yet.
 */
export interface VenueCommission {
  account: PlatformAccount | null;
  invoices: CommissionInvoice[];
}

export interface VenueInvitation {
  id: string;
  email: string;
  permissions: VenuePermission[];
  expiresAt: string;
}

/** How one hour of today is going at one venue: court-hours on sale, and used. */
export interface HourUse {
  hour: number;
  sellable: number;
  booked: number;
}

/** Today at one venue, by the venue dashboard's own rules (badPaka 2c, PRD US-15). */
export interface VenueToday {
  venueId: string;
  name: string;
  status: VenueStatus;
  keptBaht: number;
  /** The same weekday last week, as it stood at this minute. */
  lastWeekKeptBaht: number;
  bookings: number;
  /** Every booking on today's floor still standing, played or to come — and by kind. */
  todayBookings: number;
  byKind: KindCount[];
  sellableHours: number;
  bookedHours: number;
  /** Null when nothing was on sale today — not the same as none of it used. */
  usedPercent: number | null;
  hours: HourUse[];
  /** Bookings the desk could take in right now, and those whose wait has run out (US-24). */
  dueNow: number;
  pastGrace: number;
  /** Courts shut this hour for a closure (US-11). */
  shutNow: string[];
}

/** How many of today's bookings are of one kind (App / WalkIn / Series / Package). */
export interface KindCount {
  kind: string;
  count: number;
}

export interface OwnerToday {
  date: string;
  keptBaht: number;
  lastWeekKeptBaht: number;
  bookings: number;
  todayBookings: number;
  byKind: KindCount[];
  dueNow: number;
  venues: VenueToday[];
}

@Injectable({ providedIn: 'root' })
export class VenueService {
  private readonly http = inject(HttpClient);

  mine(): Observable<Venue[]> {
    return this.http.get<Venue[]>('/api/venues/mine');
  }

  /** Today at every venue this person reads the reports of (badPaka 2c). */
  today(): Observable<OwnerToday> {
    return this.http.get<OwnerToday>('/api/venues/mine/today');
  }

  /**
   * Applying to join (PRD US-10). The agreement version goes back with the application rather
   * than being assumed by the server: what was on screen is what is agreed to.
   */
  apply(application: {
    code: string;
    name: string;
    address: VenueAddress;
    business: VenueBusiness;
    agreementVersion: string;
  }): Observable<Venue> {
    return this.http.post<Venue>('/api/venues', {
      code: application.code,
      name: application.name,
      ...application.address,
      business: application.business,
      agreementVersion: application.agreementVersion,
    });
  }

  /** Which agreement the platform is asking venues to accept (PRD US-10, Q8). */
  agreement(): Observable<{ version: string }> {
    return this.http.get<{ version: string }>('/api/venues/agreement');
  }

  business(venueId: string): Observable<VenueBusiness> {
    return this.http.get<VenueBusiness>(`/api/venues/${venueId}/business`);
  }

  saveBusiness(venueId: string, business: VenueBusiness): Observable<VenueBusiness> {
    return this.http.put<VenueBusiness>(`/api/venues/${venueId}/business`, business);
  }

  /** A venue the platform turned away, asking to be looked at again (PRD US-10). */
  resubmit(venueId: string): Observable<Venue> {
    return this.http.post<Venue>(`/api/venues/${venueId}/resubmit`, null);
  }

  get(venueId: string): Observable<Venue> {
    return this.http.get<Venue>(`/api/venues/${venueId}`);
  }

  members(venueId: string): Observable<VenueMember[]> {
    return this.http.get<VenueMember[]>(`/api/venues/${venueId}/members`);
  }

  invitations(venueId: string): Observable<VenueInvitation[]> {
    return this.http.get<VenueInvitation[]>(`/api/venues/${venueId}/invitations`);
  }

  invite(
    venueId: string,
    email: string,
    permissions: readonly VenuePermission[],
  ): Observable<VenueInvitation> {
    return this.http.post<VenueInvitation>(`/api/venues/${venueId}/invitations`, {
      email,
      permissions,
    });
  }

  /**
   * What a member may do, and optionally how much they may send back in one record (PRD US-18).
   * The limit is left out where it is not being changed, so ticking a permission cannot quietly
   * reset what somebody was trusted with.
   */
  changePermissions(
    venueId: string,
    userId: string,
    permissions: readonly VenuePermission[],
    refundLimitBaht?: number,
  ): Observable<void> {
    // Left out rather than sent as null when it is not being changed: the server reads a missing
    // limit as "leave it alone", and a request that says nothing about it should look like one.
    return this.http.put<void>(`/api/venues/${venueId}/members/${userId}/permissions`, {
      permissions,
      ...(refundLimitBaht === undefined ? {} : { refundLimitBaht }),
    });
  }

  /** What this venue owes the platform, and where to send it (PRD US-21). */
  commission(venueId: string): Observable<VenueCommission> {
    return this.http.get<VenueCommission>(`/api/venues/${venueId}/commission`);
  }

  /** The owner says it has transferred, and shows something for it. */
  submitCommissionPayment(
    venueId: string,
    invoiceId: string,
    file: File,
  ): Observable<CommissionInvoice> {
    const form = new FormData();
    form.append('file', file);
    return this.http.post<CommissionInvoice>(
      `/api/venues/${venueId}/commission/${invoiceId}/payment`,
      form,
    );
  }

  removeMember(venueId: string, userId: string): Observable<void> {
    return this.http.delete<void>(`/api/venues/${venueId}/members/${userId}`);
  }

  acceptInvitation(invitationId: string, token: string): Observable<Venue> {
    return this.http.post<Venue>('/api/venues/invitations/accept', { invitationId, token });
  }
  /** Whether this member wants to hear each time a slip arrives here (PRD US-17). */
  chooseSlipEmails(venueId: string, wantsSlipEmails: boolean): Observable<void> {
    return this.http.put<void>(`/api/venues/${venueId}/notifications`, { wantsSlipEmails });
  }

  /** How much of a booking's price has to arrive before the hours are held (PRD US-28). */
  setDeposit(venueId: string, percent: number): Observable<void> {
    return this.http.put<void>(`/api/venues/${encodeURIComponent(venueId)}/deposit`, { percent });
  }

  /** How long the venue waits for somebody after their hour starts (PRD US-24). */
  setGrace(venueId: string, minutes: number): Observable<void> {
    return this.http.put<void>(`/api/venues/${encodeURIComponent(venueId)}/grace`, { minutes });
  }

  /** When this venue asks somebody for more than that, and of whom (PRD US-28). */
  setRiskRule(venueId: string, rule: VenueRiskRule): Observable<void> {
    return this.http.put<void>(`/api/venues/${encodeURIComponent(venueId)}/risk-rule`, rule);
  }
}
