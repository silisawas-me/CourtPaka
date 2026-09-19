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
}

/** What a slip may be, which the server checks again from the bytes themselves. */
export const SLIP_ACCEPT = 'image/jpeg,image/png,application/pdf';

/** Five megabytes (PRD US-04). Checked here so an obvious mistake costs no upload. */
export const SLIP_MAX_BYTES = 5 * 1024 * 1024;

/** Taking court-hours (PRD US-03). Paying for them arrives with US-04. */
@Injectable({ providedIn: 'root' })
export class BookingService {
  private readonly http = inject(HttpClient);

  hold(venueId: string, slots: BookingSlotRequest[]): Observable<Booking> {
    return this.http.post<Booking>('/api/bookings', { venueId, slots });
  }

  get(bookingId: string): Observable<Booking> {
    return this.http.get<Booking>(`/api/bookings/${bookingId}`);
  }

  /** Sends the picture of the transfer, which moves the booking into the venue's queue. */
  uploadSlip(bookingId: string, file: File): Observable<Booking> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<Booking>(`/api/bookings/${bookingId}/slip`, form);
  }
}
