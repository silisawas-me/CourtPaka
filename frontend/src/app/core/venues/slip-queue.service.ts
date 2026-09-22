import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { Booking } from '../bookings/booking.service';

/** One booking waiting for the venue to look at its slip (PRD US-12). */
export interface SlipQueueItem {
  bookingId: string;
  /** Null for a LINE account with no address; the phone is then how to reach them (US-01). */
  bookerEmail: string | null;
  bookerPhone: string | null;
  totalBaht: number;
  slipUploadedAt: string;
  startsAt: string;
  /** The hours start within the hour, so this one is worth looking at first. */
  playsSoon: boolean;
  /** The venue has been sent these exact bytes before (PRD BR-07). */
  sameSlipSeenBefore: boolean;
}

/** The venue's side of a payment: what is waiting, and the two answers (PRD US-12). */
@Injectable({ providedIn: 'root' })
export class SlipQueueService {
  private readonly http = inject(HttpClient);

  queue(venueId: string): Observable<SlipQueueItem[]> {
    return this.http.get<SlipQueueItem[]>(`/api/venues/${venueId}/slip-queue`);
  }

  /** Where the picture itself lives. Fetched as a blob, never pointed at directly. */
  slipUrl(venueId: string, bookingId: string): string {
    return `/api/venues/${venueId}/slip-queue/${bookingId}/slip`;
  }

  slip(venueId: string, bookingId: string): Observable<Blob> {
    return this.http.get(this.slipUrl(venueId, bookingId), { responseType: 'blob' });
  }

  confirm(venueId: string, bookingId: string): Observable<Booking> {
    return this.http.post<Booking>(`/api/venues/${venueId}/slip-queue/${bookingId}/confirm`, null);
  }

  reject(
    venueId: string,
    bookingId: string,
    reason: string,
    paymentReceived: boolean,
  ): Observable<Booking> {
    return this.http.post<Booking>(`/api/venues/${venueId}/slip-queue/${bookingId}/reject`, {
      reason,
      paymentReceived,
    });
  }
}
