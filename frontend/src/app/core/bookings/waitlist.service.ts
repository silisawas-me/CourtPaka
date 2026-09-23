import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

/** Where somebody stands in a queue (PRD US-27). */
export type WaitlistState = 'Waiting' | 'Offered' | 'Taken' | 'Gone';

/** One place in a queue, as the person waiting reads it. */
export interface WaitlistEntry {
  id: string;
  venueId: string;
  venueName: string;
  date: string;
  fromHour: number;
  untilHour: number;
  hours: number;
  state: WaitlistState;
  askedAt: string;
}

/** One place in a queue, as the venue reads it: somebody to ring when an hour comes back. */
export interface VenueWaitlistEntry {
  id: string;
  /** Null once the booker has asked to be forgotten (PDPA, S-15). */
  bookerEmail: string | null;
  bookerPhone: string | null;
  date: string;
  fromHour: number;
  untilHour: number;
  hours: number;
  state: WaitlistState;
  askedAt: string;
}

/** The longest run anybody may wait for, as the server has it (PRD US-27). */
export const WAITLIST_MAX_HOURS = 4;

/**
 * The queue for a day that is already sold (PRD US-27). Nothing is held and nothing is owed —
 * it is a want, and what it buys the venue is somebody to ring when an hour comes back.
 */
@Injectable({ providedIn: 'root' })
export class WaitlistService {
  private readonly http = inject(HttpClient);

  join(
    venueId: string,
    date: string,
    fromHour: number,
    untilHour: number,
    hours: number,
  ): Observable<WaitlistEntry> {
    return this.http.post<WaitlistEntry>('/api/waitlist', {
      venueId,
      date,
      fromHour,
      untilHour,
      hours,
    });
  }

  /** Everywhere this booker is waiting, soonest day first. */
  mine(): Observable<WaitlistEntry[]> {
    return this.http.get<WaitlistEntry[]>('/api/waitlist');
  }

  leave(entryId: string): Observable<void> {
    return this.http.delete<void>(`/api/waitlist/${entryId}`);
  }

  /** Who is waiting on this venue — the counter's list to ring from. */
  queue(venueId: string, date?: string): Observable<VenueWaitlistEntry[]> {
    return this.http.get<VenueWaitlistEntry[]>(`/api/venues/${venueId}/waitlist`, {
      params: date ? { date } : {},
    });
  }
}
