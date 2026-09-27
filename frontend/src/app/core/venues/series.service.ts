import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

/** A week a standing arrangement could not have, and the code saying why (PRD US-30). */
export interface SeriesMiss {
  date: string;
  /** A code, turned into words here rather than sent as a sentence by the server (US-23). */
  refusal: string;
}

/** One group that comes at the same hour every week (PRD US-30). */
export interface BookingSeries {
  seriesId: string;
  courtId: string;
  courtName: string;
  /** The weekday's name as `DayOfWeek` spells it, said in the reader's language by the page. */
  day: string;
  fromHour: number;
  untilHour: number;
  customerName: string;
  customerPhone: string | null;
  startsOn: string;
  untilOn: string | null;
  state: 'Running' | 'Ended';
  endedAt: string | null;
  endReason: string | null;
  booked: number;
  missed: SeriesMiss[];
}

export interface AgreeSeriesRequest {
  courtId: string;
  day: string;
  fromHour: number;
  untilHour: number;
  customerName: string;
  customerPhone: string | null;
  startsOn: string;
  untilOn: string | null;
}

/**
 * What stopping or changing one did. The weeks that would not go are a number somebody has to
 * look at, not one to try again (PRD US-30).
 */
export interface SeriesStopped {
  series: BookingSeries;
  cancelled: number;
  left: number;
}

/** The venue's standing arrangements (PRD US-30). */
@Injectable({ providedIn: 'root' })
export class SeriesService {
  private readonly http = inject(HttpClient);

  list(venueId: string): Observable<BookingSeries[]> {
    return this.http.get<BookingSeries[]>(`/api/venues/${venueId}/series`);
  }

  agree(venueId: string, asked: AgreeSeriesRequest): Observable<BookingSeries> {
    return this.http.post<BookingSeries>(`/api/venues/${venueId}/series`, asked);
  }

  stop(venueId: string, seriesId: string, note: string | null): Observable<SeriesStopped> {
    return this.http.post<SeriesStopped>(`/api/venues/${venueId}/series/${seriesId}/stop`, {
      note,
    });
  }

  change(venueId: string, seriesId: string, asked: AgreeSeriesRequest): Observable<SeriesStopped> {
    return this.http.post<SeriesStopped>(`/api/venues/${venueId}/series/${seriesId}/change`, asked);
  }
}
