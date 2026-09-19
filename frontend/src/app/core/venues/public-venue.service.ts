import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

export interface PublicVenue {
  id: string;
  code: string;
  name: string;
  addressLine: string;
  district: string;
  province: string;
}

/** Free to take, or outside the venue's hours. Booked arrives with US-03. */
export type HourStatus = 'Free' | 'Closed';

export interface AvailabilityHour {
  hour: number;
  status: HourStatus;
  bahtPerHour: number | null;
}

export interface CourtAvailability {
  courtId: string;
  name: string;
  hours: AvailabilityHour[];
}

export interface Availability {
  date: string;
  opensHour: number | null;
  closesHour: number | null;
  courts: CourtAvailability[];
}

/** How far ahead a booker may look, matching what the server accepts (PRD S-04). */
export const BOOKABLE_DAYS_AHEAD = 30;

/** The venues a booker can see and the hours they can take. None of it needs a session. */
@Injectable({ providedIn: 'root' })
export class PublicVenueService {
  private readonly http = inject(HttpClient);

  search(term: string): Observable<PublicVenue[]> {
    return this.http.get<PublicVenue[]>('/api/venues/search', {
      params: term ? { q: term } : {},
    });
  }

  venue(venueId: string): Observable<PublicVenue> {
    return this.http.get<PublicVenue>(`/api/venues/${venueId}/public`);
  }

  availability(venueId: string, date: string): Observable<Availability> {
    return this.http.get<Availability>(`/api/venues/${venueId}/availability`, {
      params: { date },
    });
  }
}
