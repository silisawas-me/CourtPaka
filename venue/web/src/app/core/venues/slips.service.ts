import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

/** One booking waiting for the venue to look at its slip (PRD US-12, thai-fit T5). */
export interface QueuedSlip {
  bookingId: string;
  bookerEmail: string | null;
  /** Only for a booker with no address here (a LINE account, PRD US-01). */
  bookerPhone: string | null;
  bookerName: string | null;
  totalBaht: number;
  /** What the slip should be for: the whole price, or the share the venue asks up front. */
  depositBaht: number;
  slipUploadedAt: string;
  startsAt: string;
  endsAt: string;
  courts: string[];
  playsSoon: boolean;
  /** The venue has been sent these very bytes before (BR-07). Never said to the booker. */
  sameSlipSeenBefore: boolean;
}

/**
 * The venue's slip queue (`/api/venues/{id}/slip-queue`): what is waiting, the picture, and the
 * two answers. Every call needs VerifySlip — a slip is somebody's bank account (PDPA).
 */
@Injectable({ providedIn: 'root' })
export class SlipsService {
  private readonly http = inject(HttpClient);

  queue(venueId: string): Observable<QueuedSlip[]> {
    return this.http.get<QueuedSlip[]>(`/api/venues/${venueId}/slip-queue`);
  }

  /** The newest slip of the booking, as the file the booker sent. */
  slip(venueId: string, bookingId: string): Observable<Blob> {
    return this.http.get(`/api/venues/${venueId}/slip-queue/${bookingId}/slip`, {
      responseType: 'blob',
    });
  }

  /** The money is there. A null amount means what was asked; a number is what the slip shows. */
  confirm(venueId: string, bookingId: string, amountBaht: number | null): Observable<unknown> {
    return this.http.post(`/api/venues/${venueId}/slip-queue/${bookingId}/confirm`, {
      amountBaht,
    });
  }

  /** Turned away: why, and whether the money arrived anyway — which decides what goes back. */
  reject(
    venueId: string,
    bookingId: string,
    reason: string,
    paymentReceived: boolean,
    amountBaht: number | null,
  ): Observable<unknown> {
    return this.http.post(`/api/venues/${venueId}/slip-queue/${bookingId}/reject`, {
      reason,
      paymentReceived,
      amountBaht,
    });
  }
}
