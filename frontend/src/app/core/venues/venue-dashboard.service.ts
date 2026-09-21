import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

/** One day of a venue's figures, counted on the day it is played (PRD US-15). */
export interface DashboardDay {
  date: string;
  onlineBaht: number;
  staffBaht: number;
  sellableHours: number;
  bookedHours: number;
}

export interface DashboardMonth {
  year: number;
  month: number;
  onlineBaht: number;
  staffBaht: number;
}

/** What is waiting for somebody at the venue, whatever the range (PRD US-15). */
export interface DashboardAttention {
  slipsToCheck: number;
  paymentsUnanswered: number;
  refundsOutstanding: number;
}

/**
 * A venue's figures for a range of days. Every number is the server's: what counts as kept, which
 * hours were for sale, and what is still to come are rules about money (PRD 6.2), and the page
 * only lays them out.
 */
export interface Dashboard {
  from: string;
  to: string;
  onlineBaht: number;
  staffBaht: number;
  advanceBaht: number;
  sellableHours: number;
  bookedHours: number;
  /** Null when there was nothing to sell, which is not the same as selling none of it. */
  utilizationPercent: number | null;
  days: DashboardDay[];
  months: DashboardMonth[];
  attention: DashboardAttention;
}

@Injectable({ providedIn: 'root' })
export class VenueDashboardService {
  private readonly http = inject(HttpClient);

  /** The range as the API names days (YYYY-MM-DD); either end may be left to the server. */
  read(venueId: string, from?: string, to?: string): Observable<Dashboard> {
    let params = new HttpParams();
    if (from) {
      params = params.set('from', from);
    }
    if (to) {
      params = params.set('to', to);
    }

    return this.http.get<Dashboard>(`/api/venues/${venueId}/dashboard`, { params });
  }
}
