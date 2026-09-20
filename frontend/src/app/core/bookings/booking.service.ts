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

export type BookingStatus =
  | 'Held'
  | 'PendingVerification'
  | 'Confirmed'
  | 'Completed'
  | 'Cancelled'
  | 'Expired'
  | 'Rejected'
  | 'NoShow';

/**
 * What letting a booking go right now would come to, worked out by the server so the number shown
 * before the button and the number given after it are the same one (PRD US-05).
 */
export interface CancellationOffer {
  allowed: boolean;
  /** The share of the booking this would give back, which a tiered policy makes worth saying. */
  refundPercent: number;
  refundBaht: number;
  /** The venue has yet to say whether the money arrived, so the amount is not settled. */
  awaitsVenue: boolean;
}

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
  /** When the booker last sent a slip, if they have (PRD US-04). */
  slipUploadedAt: string | null;
  /** Whether the venue has the money, and what it owes back (PRD 6.2). */
  paymentState: 'NotReceived' | 'Received' | 'Unconfirmed';
  refundDueBaht: number;
  refundedBaht: number;
  cancellation: CancellationOffer;
}

/** A booker's own bookings, split by the server, which holds the clock (PRD US-05). */
export interface BookingHistory {
  upcoming: Booking[];
  past: Booking[];
}

/** What a slip may be, which the server checks again from the bytes themselves. */
export const SLIP_ACCEPT = 'image/jpeg,image/png,application/pdf';

/** Five megabytes (PRD US-04). Checked here so an obvious mistake costs no upload. */
export const SLIP_MAX_BYTES = 5 * 1024 * 1024;

/** Taking court-hours, paying for them, and letting them go (PRD US-03, US-04, US-05). */
@Injectable({ providedIn: 'root' })
export class BookingService {
  private readonly http = inject(HttpClient);

  hold(venueId: string, slots: BookingSlotRequest[]): Observable<Booking> {
    return this.http.post<Booking>('/api/bookings', { venueId, slots });
  }

  get(bookingId: string): Observable<Booking> {
    return this.http.get<Booking>(`/api/bookings/${bookingId}`);
  }

  /** Everything this booker has taken, already split into ahead and behind (PRD US-05). */
  mine(): Observable<BookingHistory> {
    return this.http.get<BookingHistory>('/api/bookings');
  }

  cancel(bookingId: string): Observable<Booking> {
    return this.http.post<Booking>(`/api/bookings/${bookingId}/cancel`, null);
  }

  /** Sends the picture of the transfer, which moves the booking into the venue's queue. */
  uploadSlip(bookingId: string, file: File): Observable<Booking> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<Booking>(`/api/bookings/${bookingId}/slip`, form);
  }
}
