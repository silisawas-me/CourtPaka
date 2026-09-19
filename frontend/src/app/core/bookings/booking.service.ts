import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

/** One court-hour, named the way the grid names it. */
export interface BookingSlotRequest {
  courtId: string;
  date: string;
  hour: number;
}

export interface BookingSlot extends BookingSlotRequest {
  courtName: string;
  bahtPerHour: number;
}

export type BookingStatus = 'Held' | 'PendingVerification' | 'Confirmed';

export interface Booking {
  id: string;
  venueId: string;
  venueName: string;
  status: BookingStatus;
  createdAt: string;
  /** When the hold lapses if it has not been paid for (PRD BR-02). */
  holdExpiresAt: string;
  totalBaht: number;
  slots: BookingSlot[];
}

/** Taking court-hours (PRD US-03). Paying for them arrives with US-04. */
@Injectable({ providedIn: 'root' })
export class BookingService {
  private readonly http = inject(HttpClient);

  hold(venueId: string, slots: BookingSlotRequest[]): Observable<Booking> {
    return this.http.post<Booking>('/api/bookings', { venueId, slots });
  }
}
