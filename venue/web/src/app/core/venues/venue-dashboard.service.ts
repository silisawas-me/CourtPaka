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
  /** The counter's shop that day, rung up less handed back (owner app PR-5). */
  shopBaht: number;
}

export interface DashboardMonth {
  year: number;
  month: number;
  onlineBaht: number;
  staffBaht: number;
  /** Bookings the money came from: paid for, and played in that month. */
  bookings: number;
  /** What those bookings left the venue owing back, and what it has sent so far (PRD 7.3). */
  refundDueBaht: number;
  refundedBaht: number;
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
/**
 * Hours the venue had sold and lost, and how many of them went again (PRD US-27). A hold that
 * ran out is not counted as a loss — nobody ever bought it.
 */
export interface Recovery {
  hoursLost: number;
  hoursRefilled: number;
  refilledBaht: number;
  hoursFromQueue: number;
  fromQueueBaht: number;
}

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
  recovery: Recovery;
  /** Hours sold and not yet given (PRD US-31). Never part of the money above. */
  owedHours: OwedHours;
  /** The counter's other trade, and the money that went out (PRD US-32, US-33). */
  trade: Trade;
  /** Every receipt counted in the range, by how it was paid (owner app PR-5). */
  byMethod: DashboardMethod[];
  /** The shop's best sellers over the range, most money first. */
  topItems: DashboardItem[];
  /** Court plus shop money over the same number of days just before the range. */
  priorBaht: number;
}

/**
 * What the counter sold besides court time and what the venue paid out (PRD US-32, US-33). Kept
 * apart from the court money because it is a different business with a different margin.
 */
export interface DashboardMethod {
  method: 'Cash' | 'PromptPay' | 'Card';
  baht: number;
}

export interface DashboardItem {
  itemId: string;
  name: string;
  quantity: number;
  baht: number;
}

export interface Trade {
  shopBaht: number;
  spentBaht: number;
  /** All takings less what went out. Not profit in an accounting sense (⚠️ S-30). */
  leftOverBaht: number;
}

/**
 * Hours the venue has been paid for and not yet given (PRD US-31). Not revenue: the money came
 * in when the packages were sold, and what the venue has in exchange is an obligation.
 */
export interface OwedHours {
  hours: number;
  baht: number;
  packages: number;
  /** Of those, the ones whose hours are about to run out. */
  runningOut: number;
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
