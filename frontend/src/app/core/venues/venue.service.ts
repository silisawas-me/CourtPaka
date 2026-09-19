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
}

export interface VenueMember {
  userId: string;
  email: string;
  role: 'Owner' | 'Staff';
  permissions: VenuePermission[];
}

export interface VenueInvitation {
  id: string;
  email: string;
  permissions: VenuePermission[];
  expiresAt: string;
}

@Injectable({ providedIn: 'root' })
export class VenueService {
  private readonly http = inject(HttpClient);

  mine(): Observable<Venue[]> {
    return this.http.get<Venue[]>('/api/venues/mine');
  }

  create(code: string, name: string, address: VenueAddress): Observable<Venue> {
    return this.http.post<Venue>('/api/venues', { code, name, ...address });
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

  changePermissions(
    venueId: string,
    userId: string,
    permissions: readonly VenuePermission[],
  ): Observable<void> {
    return this.http.put<void>(`/api/venues/${venueId}/members/${userId}/permissions`, {
      permissions,
    });
  }

  removeMember(venueId: string, userId: string): Observable<void> {
    return this.http.delete<void>(`/api/venues/${venueId}/members/${userId}`);
  }

  acceptInvitation(invitationId: string, token: string): Observable<Venue> {
    return this.http.post<Venue>('/api/venues/invitations/accept', { invitationId, token });
  }
}
