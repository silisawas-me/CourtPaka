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

export interface VenueInvitation {
  id: string;
  email: string;
  permissions: VenuePermission[];
  expiresAt: string;
}

/**
 * What is waiting at a venue for the person asking (PRD US-17). Each number is counted only for
 * somebody who could do something about it, so a member who checks slips is not shown the money.
 */
export interface VenueAttention {
  slipsToCheck: number;
  bookingsWithMoneyWaiting: number;
}

@Injectable({ providedIn: 'root' })
export class VenueService {
  private readonly http = inject(HttpClient);

  mine(): Observable<Venue[]> {
    return this.http.get<Venue[]>('/api/venues/mine');
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

  removeMember(venueId: string, userId: string): Observable<void> {
    return this.http.delete<void>(`/api/venues/${venueId}/members/${userId}`);
  }

  acceptInvitation(invitationId: string, token: string): Observable<Venue> {
    return this.http.post<Venue>('/api/venues/invitations/accept', { invitationId, token });
  }
  /** What is waiting here for the person asking (PRD US-17). */
  attention(venueId: string): Observable<VenueAttention> {
    return this.http.get<VenueAttention>(`/api/venues/${encodeURIComponent(venueId)}/attention`);
  }

  /** Whether this member wants to hear each time a slip arrives here (PRD US-17). */
  chooseSlipEmails(venueId: string, wantsSlipEmails: boolean): Observable<void> {
    return this.http.put<void>(`/api/venues/${venueId}/notifications`, { wantsSlipEmails });
  }

  /** How much of a booking's price has to arrive before the hours are held (PRD US-28). */
  setDeposit(venueId: string, percent: number): Observable<void> {
    return this.http.put<void>(`/api/venues/${encodeURIComponent(venueId)}/deposit`, { percent });
  }

  /** When this venue asks somebody for more than that, and of whom (PRD US-28). */
  setRiskRule(venueId: string, rule: VenueRiskRule): Observable<void> {
    return this.http.put<void>(`/api/venues/${encodeURIComponent(venueId)}/risk-rule`, rule);
  }
}
