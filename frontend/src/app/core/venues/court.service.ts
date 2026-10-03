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

/** A venue opens, closes and charges on the hour: 0 starts the day, 24 is midnight at its end. */
export const OPENING_HOURS = Array.from({ length: 24 }, (_, hour) => hour);
export const CLOSING_HOURS = OPENING_HOURS.map((hour) => hour + 1);

export interface Court {
  id: string;
  name: string;
  position: number;
  /** Whether the court is in use on the date that was asked about, today unless given. */
  isActive: boolean;
}

/** Both hours null means the venue does not open that day. */
export interface OpeningHoursDay {
  day: Weekday;
  opensHour: number | null;
  closesHour: number | null;
}

export interface CourtStatusChange {
  active: boolean;
  effectiveFrom: string;
  changedAt: string;
}

/** What the court's timeline says after a change, which is not always what was asked for. */
export interface CourtStatus {
  courtId: string;
  activeToday: boolean;
  scheduled: CourtStatusChange[];
}

export interface OpeningHours {
  id: string;
  effectiveFrom: string;
  /** Whether this is the week the venue is running on today, rather than one dated ahead. */
  inForce: boolean;
  days: OpeningHoursDay[];
}

/** A court shut for a while and back by itself (PRD US-11), not taken out of use. */
export interface CourtClosure {
  id: string;
  courtId: string;
  startsOn: string;
  startHour: number;
  endsOn: string;
  endHour: number;
  reason: string;
  createdAt: string;
  liftedAt: string | null;
}

export interface CloseCourtRequest {
  startsOn: string;
  startHour: number;
  endsOn: string;
  endHour: number;
  reason: string;
}

/** A booking a closure would displace: where and when, never who (PDPA). */
export interface BlockingBooking {
  bookingId: string;
  courtId: string;
  courtName: string;
  date: string;
  fromHour: number;
  toHour: number;
  status: string;
}

@Injectable({ providedIn: 'root' })
export class CourtService {
  private readonly http = inject(HttpClient);

  courts(venueId: string, on?: string): Observable<Court[]> {
    return this.http.get<Court[]>(`/api/venues/${venueId}/courts`, {
      params: on ? { on } : {},
    });
  }

  addCourt(venueId: string, name: string): Observable<Court> {
    return this.http.post<Court>(`/api/venues/${venueId}/courts`, { name });
  }

  updateCourt(venueId: string, courtId: string, name: string, position: number): Observable<Court> {
    return this.http.put<Court>(`/api/venues/${venueId}/courts/${courtId}`, { name, position });
  }

  changeCourtStatus(venueId: string, courtId: string, active: boolean): Observable<CourtStatus> {
    return this.http.put<CourtStatus>(`/api/venues/${venueId}/courts/${courtId}/status`, {
      active,
    });
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

  closures(venueId: string): Observable<CourtClosure[]> {
    return this.http.get<CourtClosure[]>(`/api/venues/${venueId}/closures`);
  }

  /** Refused with the bookings in the way (`bookings` on the problem) when there are any. */
  closeCourt(venueId: string, courtId: string, asked: CloseCourtRequest): Observable<CourtClosure> {
    return this.http.post<CourtClosure>(`/api/venues/${venueId}/courts/${courtId}/closures`, asked);
  }

  liftClosure(venueId: string, closureId: string): Observable<CourtClosure> {
    return this.http.post<CourtClosure>(`/api/venues/${venueId}/closures/${closureId}/lift`, null);
  }
}
