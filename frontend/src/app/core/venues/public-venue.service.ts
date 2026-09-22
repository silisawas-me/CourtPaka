import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { VenueAddress } from './venue.service';

export interface PublicVenue extends VenueAddress {
  id: string;
  name: string;
}

/** Free to take, outside the venue's hours, or already someone else's. */
export type HourStatus = 'Free' | 'Closed' | 'Booked';

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
  venue: PublicVenue;
  date: string;
  /** The last day the server will accept, so the picker offers exactly those days (PRD S-04). */
  lastBookableDate: string;
  opensHour: number | null;
  closesHour: number | null;
  courts: CourtAvailability[];
}

/** The venues a booker can see and the hours they can take. None of it needs a session. */
@Injectable({ providedIn: 'root' })
export class PublicVenueService {
  private readonly http = inject(HttpClient);

  search(term: string): Observable<PublicVenue[]> {
    return this.http.get<PublicVenue[]>('/api/venues/search', {
      params: term ? { q: term } : {},
    });
  }

  /** The day's grid and the venue it belongs to, which is everything the grid page draws. */
  /**
   * One day's grid. `refresh` marks a read of a day already on screen, which the server does not
   * count as another view of the venue (PRD 8 `venue_page_viewed`).
   */
  availability(venueId: string, date: string, refresh = false): Observable<Availability> {
    return this.http.get<Availability>(`/api/venues/${venueId}/availability`, {
      params: refresh ? { date, refresh: true } : { date },
    });
  }
}
