import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable, shareReplay } from 'rxjs';
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

function key(venueId: string, date: string): string {
  return `${venueId}@${date}`;
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

  /**
   * A day asked for before the page that draws it exists (see {@link prefetch}). One answer, kept
   * for whoever asks for that day first, and only that once.
   */
  private readonly started = new Map<string, { asked: number; answer: Observable<Availability> }>();

  /**
   * How long an answer asked for at boot is still the answer. Past that, whoever opens that day
   * is opening it again, not still waiting for it.
   */
  private static readonly StaysFreshMs = 30_000;

  /**
   * One day's grid and the venue it belongs to, which is everything the grid page draws.
   *
   * `refresh` marks a read of a day already on screen, which the server does not count as another
   * view of the venue (PRD 8 `venue_page_viewed`).
   */
  availability(venueId: string, date: string, refresh = false): Observable<Availability> {
    const waiting = refresh ? undefined : this.started.get(key(venueId, date));
    if (waiting) {
      // Asked once, drawn once: a refresh a minute later must reach the server, not this.
      this.started.delete(key(venueId, date));
      if (Date.now() - waiting.asked < PublicVenueService.StaysFreshMs) {
        return waiting.answer;
      }
    }

    return this.http.get<Availability>(`/api/venues/${venueId}/availability`, {
      params: refresh ? { date, refresh: true } : { date },
    });
  }

  /**
   * Starts the day's request before anything asks for it, which at boot is before the page that
   * draws it has even been downloaded (PRD 8's LCP target, US-02).
   *
   * Without this the wait is one thing after another: the app starts, the router fetches the
   * grid's chunk, the page it builds then asks for the day. The request does not depend on any of
   * that, so it goes out first and arrives while the rest is still loading.
   */
  prefetch(venueId: string, date: string): void {
    const answer = this.http
      .get<Availability>(`/api/venues/${venueId}/availability`, { params: { date } })
      .pipe(shareReplay({ bufferSize: 1, refCount: false }));

    // Subscribed here, or nothing would go out until the page asked — which is the wait this
    // exists to remove. If it fails, shareReplay drops what it held, so the page that asks next
    // sends its own request and shows its own answer; nothing inherits a stale failure.
    answer.subscribe({ error: () => undefined });
    this.started.set(key(venueId, date), { asked: Date.now(), answer });
  }
}
