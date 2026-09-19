import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

/** Monday first, the way a Thai week is read on a schedule. */
export const WEEKDAYS = [
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
  'Sunday',
] as const;

export type Weekday = (typeof WEEKDAYS)[number];

export interface Court {
  id: string;
  name: string;
  position: number;
  isActive: boolean;
}

export interface CourtStatusChange {
  active: boolean;
  effectiveFrom: string;
  changedAt: string;
}

/** Both hours null means the venue does not open that day. */
export interface OpeningHoursDay {
  day: Weekday;
  opensHour: number | null;
  closesHour: number | null;
}

export interface OpeningHours {
  id: string;
  effectiveFrom: string;
  /** Whether this is the week the venue is running on today, rather than one dated ahead. */
  inForce: boolean;
  days: OpeningHoursDay[];
}

@Injectable({ providedIn: 'root' })
export class CourtService {
  private readonly http = inject(HttpClient);

  courts(venueId: string): Observable<Court[]> {
    return this.http.get<Court[]>(`/api/venues/${venueId}/courts`);
  }

  addCourt(venueId: string, name: string): Observable<Court> {
    return this.http.post<Court>(`/api/venues/${venueId}/courts`, { name });
  }

  updateCourt(venueId: string, courtId: string, name: string, position: number): Observable<Court> {
    return this.http.put<Court>(`/api/venues/${venueId}/courts/${courtId}`, { name, position });
  }

  changeCourtStatus(venueId: string, courtId: string, active: boolean): Observable<Court> {
    return this.http.put<Court>(`/api/venues/${venueId}/courts/${courtId}/status`, { active });
  }

  courtHistory(venueId: string, courtId: string): Observable<CourtStatusChange[]> {
    return this.http.get<CourtStatusChange[]>(
      `/api/venues/${venueId}/courts/${courtId}/status-history`,
    );
  }

  openingHours(venueId: string): Observable<OpeningHours[]> {
    return this.http.get<OpeningHours[]>(`/api/venues/${venueId}/opening-hours`);
  }

  setOpeningHours(
    venueId: string,
    effectiveFrom: string,
    days: readonly OpeningHoursDay[],
  ): Observable<OpeningHours> {
    return this.http.put<OpeningHours>(`/api/venues/${venueId}/opening-hours`, {
      effectiveFrom,
      days,
    });
  }
}
