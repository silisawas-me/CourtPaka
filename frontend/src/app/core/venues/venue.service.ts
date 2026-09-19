import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export type VenueStatus = 'Pending' | 'Approved' | 'Rejected' | 'Suspended';

/** Mirrors VenuePermissions on the API; the owner holds all of them implicitly. */
export const VENUE_PERMISSIONS = [
  'VerifySlip',
  'ManageBookings',
  'CloseCourt',
  'ViewReports',
  'ManageSettings',
] as const;

export type VenuePermission = (typeof VENUE_PERMISSIONS)[number];

/** Flag values must match the API's [Flags] enum, which is what the endpoints accept. */
const PERMISSION_FLAGS: Record<VenuePermission, number> = {
  VerifySlip: 1,
  ManageBookings: 2,
  CloseCourt: 4,
  ViewReports: 8,
  ManageSettings: 16,
};

export function toPermissionFlags(permissions: readonly VenuePermission[]): number {
  return permissions.reduce((flags, permission) => flags | PERMISSION_FLAGS[permission], 0);
}

export interface Venue {
  id: string;
  code: string;
  name: string;
  status: VenueStatus;
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

  create(code: string, name: string): Observable<Venue> {
    return this.http.post<Venue>('/api/venues', { code, name });
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
      permissions: toPermissionFlags(permissions),
    });
  }

  changePermissions(
    venueId: string,
    userId: string,
    permissions: readonly VenuePermission[],
  ): Observable<void> {
    return this.http.put<void>(`/api/venues/${venueId}/members/${userId}/permissions`, {
      permissions: toPermissionFlags(permissions),
    });
  }

  removeMember(venueId: string, userId: string): Observable<void> {
    return this.http.delete<void>(`/api/venues/${venueId}/members/${userId}`);
  }

  acceptInvitation(invitationId: string, token: string): Observable<Venue> {
    return this.http.post<Venue>('/api/venues/invitations/accept', { invitationId, token });
  }
}
