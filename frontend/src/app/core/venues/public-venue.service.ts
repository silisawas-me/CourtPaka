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
  /**
   * Where the venue's day starts (thai-fit T4): 0 unless it stays open past midnight. Hours 24
   * and up are after midnight, and "now" at 01:00 is the day before's hour 25 when this is 2.
   */
  dayStartsHour?: number;
}

/**
 * A venue's day of hours: which courts, which hours are on sale and at what price. The venue's
 * own screens draw their floor from it (day console, "now" board, counter sale). The booker's
 * pages that it was first written for were taken out on 2026-09-28 (docs/plan/cut-booker.md);
 * the endpoint is still public.
 */
@Injectable({ providedIn: 'root' })
export class PublicVenueService {
  private readonly http = inject(HttpClient);

  /**
   * One day's grid and the venue it belongs to.
   *
   * `refresh` marks a read of a day already on screen, which the server does not count as another
   * view of the venue (PRD 8 `venue_page_viewed`).
   */
  availability(venueId: string, date: string, refresh = false): Observable<Availability> {
    return this.http.get<Availability>(`/api/venues/${venueId}/availability`, {
      params: refresh ? { date, refresh: true } : { date },
    });
  }
}
