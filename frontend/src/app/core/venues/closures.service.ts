import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

/** One stretch a court is shut for (PRD US-11). */
export interface CourtClosure {
  id: string;
  courtId: string;
  startsOn: string;
  startHour: number;
  endsOn: string;
  endHour: number;
  reason: string;
  createdAt: string;
  /** When it was ended early, if it was. A lifted closure still shows: it explains a quiet day. */
  liftedAt: string | null;
}

/**
 * One booking standing in the way of a closure. No booker on it — who booked is behind another
 * permission, so this screen is told what it has to deal with and where (PDPA, PRD 8, US-14).
 */
export interface ClashingBooking {
  bookingId: string;
  courtId: string;
  courtName: string;
  date: string;
  fromHour: number;
  toHour: number;
  status: string;
}

export interface CloseCourtRequest {
  startsOn: string;
  startHour: number;
  endsOn: string;
  endHour: number;
  reason: string;
}

/** Shutting a court and opening it again (PRD US-11). */
@Injectable({ providedIn: 'root' })
export class ClosuresService {
  private readonly http = inject(HttpClient);

  list(venueId: string): Observable<CourtClosure[]> {
    return this.http.get<CourtClosure[]>(`/api/venues/${venueId}/closures`);
  }

  close(venueId: string, courtId: string, stretch: CloseCourtRequest): Observable<CourtClosure> {
    return this.http.post<CourtClosure>(
      `/api/venues/${venueId}/courts/${courtId}/closures`,
      stretch,
    );
  }

  lift(venueId: string, closureId: string): Observable<CourtClosure> {
    return this.http.post<CourtClosure>(`/api/venues/${venueId}/closures/${closureId}/lift`, null);
  }
}
